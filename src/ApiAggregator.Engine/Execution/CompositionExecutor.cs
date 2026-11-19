using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Logging;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiAggregator.Engine.Execution;

/// <summary>
/// Runs a recipe:
///   1. validate caller inputs            (400 if invalid)
///   2. serve from response cache          (if enabled)
///   3. run steps level by level           (steps in a level run in parallel)
///   3b. build the "combine" lists          (merged records, after all steps)
///   4. build the response from the template
///   5. required step failed -> 502; optional step failed -> 200 with "_errors"
/// </summary>
public sealed class CompositionExecutor(
    IConfigStore store,
    DownstreamAuthenticator authenticator,
    IHttpClientFactory httpClientFactory,
    IResponseCache cache,
    IOptions<EngineOptions> options,
    ILogger<CompositionExecutor> logger)
{
    private readonly EngineOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, ExecutionPlan> _plans = new(StringComparer.Ordinal);

    public async Task<CompositionResult> ExecuteAsync(Recipe recipe, InboundRequest inbound, CallerContext caller, ExecutionMode mode, CancellationToken ct)
    {
        // Every log line written during this run carries these fields.
        using var scope = logger.BeginScope(new LogScope(("CorrelationId", caller.CorrelationId), ("RecipeId", recipe.Id), ("RecipeVersion", recipe.Version)));
        return await RunAsync(recipe, inbound, caller, mode, ct);
    }

    private async Task<CompositionResult> RunAsync(Recipe recipe, InboundRequest inbound, CallerContext caller, ExecutionMode mode, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var config = await store.GetSnapshotAsync(ct);
        logger.CompositionStarted(recipe.Id, recipe.Version, caller.Subject ?? "anonymous");

        // 1. inputs
        var (request, inputErrors) = InputBinder.Bind(recipe, inbound, caller);
        if (inputErrors.Count > 0)
        {
            logger.InputRejected(recipe.Id, inputErrors.Count); // the count only: input values can be personal data
            var errors = new JsonArray(inputErrors.Select(e => (JsonNode?)new JsonObject { ["code"] = ErrorBody.InvalidInput, ["message"] = e }).ToArray());
            return new CompositionResult
            {
                StatusCode = 400,
                Body = ErrorBody.Problem(ErrorBody.InvalidInput, "Some inputs are missing or invalid.", 400, caller.CorrelationId, errors: errors),
                ErrorCode = ErrorBody.InvalidInput,
                DurationMs = clock.ElapsedMilliseconds
            };
        }

        // 2. response cache
        string? responseKey = null;
        if (mode.UseCache && recipe.Cache.Enabled)
        {
            var callerKey = CacheKeys.CallerPart(recipe.Cache.PerCaller, caller.Subject);
            responseKey = CacheKeys.Response(recipe.Id, recipe.Version, config.Version, callerKey, request.ToJsonString());
            if (cache.TryGet(responseKey, out var cached))
            {
                logger.ResponseCacheHit("recipe", recipe.Id);
                logger.CompositionCompleted(recipe.Id, recipe.Version, 200, clock.ElapsedMilliseconds, "", true, false);
                return new CompositionResult { StatusCode = 200, Body = JsonNode.Parse(cached), FromCache = true, DurationMs = clock.ElapsedMilliseconds };
            }
        }

        // 3. steps
        var plan = _plans.GetOrAdd($"{recipe.Id}:{recipe.Version}:{config.Version}", _ => ExecutionPlanner.Plan(recipe));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(recipe.TimeoutMs);

        var roots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["request"] = request,
            ["ctx"] = new JsonObject { ["correlationId"] = caller.CorrelationId, ["subject"] = caller.Subject }
        };
        var runs = new Dictionary<string, StepRun>(StringComparer.Ordinal);

        foreach (var level in plan.Levels)
        {
            // Roots are only read while a level runs and only written between levels: no locking needed.
            var scope = new Scope(roots);
            var results = await Task.WhenAll(level.Select(step => RunStepAsync(step, plan, runs, scope, config, caller, mode, timeout.Token, ct)));
            foreach (var run in results)
            {
                runs[run.Step.Alias] = run;
                roots[run.Step.Alias] = run.Output;
            }
        }

        var trace = recipe.Steps.Select(s => runs[s.Alias].Trace).ToList();
        var stepSummary = string.Join(",", trace.Select(t => $"{t.Alias}:{t.Outcome}"));

        // 4/5. response
        var failures = runs.Values.Where(r => r.Failed).ToList();
        var requiredFailures = failures.Where(r => r.Step.Required).ToList();
        // Report root causes only, not the steps that were skipped because of them.
        var rootCauses = failures.Where(f => !f.Cascaded).ToList();
        CompositionResult result;

        if (requiredFailures.Count > 0)
        {
            result = FailedResult(rootCauses, caller, trace, clock.ElapsedMilliseconds);
            logger.CompositionFailed(recipe.Id, recipe.Version, result.StatusCode, result.ErrorCode,
                string.Join(",", rootCauses.Select(r => $"{r.Step.Alias}:{r.Trace.ErrorCode}")), result.DurationMs);
        }
        else
        {
            // Combined lists are built once every step has finished; the response template can use them as {{combine.<name>}}.
            var combines = RunCombines(recipe, roots, mode);
            trace.AddRange(combines.Select(c => c.Trace));
            if (combines.Count > 0)
                roots["combine"] = new JsonObject(combines.Select(c => KeyValuePair.Create(c.Name, (JsonNode?)c.Records)));
            var combineFailures = combines.Where(c => c.Failed).ToList();

            if (combineFailures.Any(c => c.Required))
            {
                result = CombineFailedResult(combineFailures, caller, trace, clock.ElapsedMilliseconds);
                logger.CompositionFailed(recipe.Id, recipe.Version, result.StatusCode, result.ErrorCode,
                    string.Join(",", combineFailures.Select(c => $"{c.Trace.Alias}:{c.Trace.ErrorCode}")), result.DurationMs);
                return result;
            }

            var body = recipe.Response is not null
                ? Template.ResolveNode(recipe.Response, new Scope(roots))
                : DefaultResponse(recipe, runs, combines);

            var partial = failures.Count > 0 || combineFailures.Count > 0;
            if (partial && body is JsonObject obj)
                obj["_errors"] = new JsonArray(rootCauses.Select(f => (JsonNode?)ErrorEntry(f))
                    .Concat(combineFailures.Select(c => (JsonNode?)ErrorEntry(c))).ToArray());

            result = new CompositionResult { StatusCode = 200, Body = body, Partial = partial, Trace = trace, DurationMs = clock.ElapsedMilliseconds };

            if (responseKey is not null && !partial && body is not null)
                cache.Set(responseKey, Encoding.UTF8.GetBytes(body.ToJsonString()), TimeSpan.FromSeconds(recipe.Cache.TtlSeconds));

            // Audit line: what ran and how it ended. Never values.
            logger.CompositionCompleted(recipe.Id, recipe.Version, result.StatusCode, result.DurationMs, stepSummary, false, partial);
        }

        return result;
    }

    /// <summary>The 502/504 response for a failed required step, as RFC 7807 problem details.</summary>
    private static CompositionResult FailedResult(List<StepRun> rootCauses, CallerContext caller, List<StepTrace> trace, long durationMs)
    {
        var first = rootCauses[0].Trace;
        var retryable = rootCauses.Any(r => r.Trace.Retryable);

        // 504 only when every root cause was a timeout; anything else is a bad gateway.
        var status = rootCauses.All(r => r.Trace.ErrorCode == DownstreamErrorCode.Timeout) ? 504 : 502;
        var title = DownstreamErrorCode.IsAuthProblem(first.ErrorCode)
            ? "The gateway couldn't authenticate with a required service."
            : "A required service call failed.";

        var body = ErrorBody.Problem(first.ErrorCode!, title, status, caller.CorrelationId,
            detail: $"{first.DisplayName ?? first.Alias}: {first.Message}",
            retryable: retryable,
            errors: new JsonArray(rootCauses.Select(r => (JsonNode?)ErrorEntry(r)).ToArray()));

        return new CompositionResult
        {
            StatusCode = status,
            Body = body,
            ErrorCode = first.ErrorCode,
            Retryable = retryable,
            Trace = trace,
            DurationMs = durationMs
        };
    }

    /// <summary>One entry of "errors" / "_errors". Only safe, caller-facing text: never the internal detail.</summary>
    private static JsonObject ErrorEntry(StepRun run) => new()
    {
        ["step"] = run.Step.Alias,
        ["name"] = run.Step.DisplayName ?? run.Step.Alias,
        ["service"] = run.Trace.ServiceId,
        ["code"] = run.Trace.ErrorCode,
        ["status"] = run.Trace.HttpStatus,
        ["message"] = run.Trace.Message,
        ["retryable"] = run.Trace.Retryable
    };

    // ------------------------------------------------------------------ combine

    /// <summary>Builds every combined list, in the order they are declared in the recipe.</summary>
    private List<CombineRun> RunCombines(Recipe recipe, IReadOnlyDictionary<string, JsonNode?> roots, ExecutionMode mode)
    {
        var runs = new List<CombineRun>();
        var scope = new Scope(roots);

        foreach (var (name, def) in recipe.Combine)
        {
            var clock = Stopwatch.StartNew();
            var alias = $"combine.{name}";
            var title = $"Combine {name}";

            CombineOutcome outcome;
            try { outcome = RecordCombiner.Combine(name, def, scope); }
            catch (FormatException) // the validator stops this earlier; this keeps a bad saved recipe from crashing a call
            {
                outcome = new CombineOutcome(null, null, $"The combined list '{name}' has a source that isn't a valid path.");
            }

            if (outcome.Records is { } records && outcome.Stats is { } stats)
            {
                logger.CombineCompleted(name, stats.InputsText, stats.Matched, stats.Unmapped, stats.Ambiguous, stats.WithoutKey, stats.Ignored, stats.Output, clock.ElapsedMilliseconds);
                // The stats text only goes into the trace for admin test runs (counts and source names, never values).
                var trace = new StepTrace(alias, title, StepOutcome.Succeeded, null, clock.ElapsedMilliseconds, 0, false, null,
                    Detail: mode.IncludeRequestDetails ? stats.Describe() : null);
                runs.Add(new CombineRun(name, def.Required, records, trace));
            }
            else
            {
                logger.CombineFailed(name, outcome.Error!);
                var trace = new StepTrace(alias, title, StepOutcome.Failed, null, clock.ElapsedMilliseconds, 0, false, outcome.Error,
                    ErrorCode: DownstreamErrorCode.CombineFailed);
                runs.Add(new CombineRun(name, def.Required, null, trace));
            }
        }
        return runs;
    }

    /// <summary>The 502 for a required combine that couldn't be produced.</summary>
    private static CompositionResult CombineFailedResult(List<CombineRun> failures, CallerContext caller, List<StepTrace> trace, long durationMs)
    {
        var body = ErrorBody.Problem(DownstreamErrorCode.CombineFailed, "Records from several services couldn't be combined.", 502, caller.CorrelationId,
            detail: failures[0].Trace.Message, retryable: false,
            errors: new JsonArray(failures.Select(f => (JsonNode?)ErrorEntry(f)).ToArray()));

        return new CompositionResult
        {
            StatusCode = 502,
            Body = body,
            ErrorCode = DownstreamErrorCode.CombineFailed,
            Retryable = false,
            Trace = trace,
            DurationMs = durationMs
        };
    }

    /// <summary>Same shape as a step's entry, so callers handle both the same way. A combine has no service and no HTTP status.</summary>
    private static JsonObject ErrorEntry(CombineRun run) => new()
    {
        ["step"] = run.Trace.Alias,
        ["name"] = run.Trace.DisplayName,
        ["service"] = null,
        ["code"] = DownstreamErrorCode.CombineFailed,
        ["status"] = null,
        ["message"] = run.Trace.Message,
        ["retryable"] = false
    };

    // ------------------------------------------------------------------ steps

    private async Task<StepRun> RunStepAsync(StepDef step, ExecutionPlan plan, IReadOnlyDictionary<string, StepRun> previous,
        Scope scope, ConfigSnapshot config, CallerContext caller, ExecutionMode mode, CancellationToken token, CancellationToken callerToken)
    {
        var clock = Stopwatch.StartNew();

        // "serviceId/endpointId": known from the recipe alone, so even a failed lookup can be reported.
        var slash = step.Endpoint.IndexOf('/');
        var serviceId = slash > 0 ? step.Endpoint[..slash] : step.Endpoint;
        var endpointId = slash > 0 ? step.Endpoint[(slash + 1)..] : "";
        var serviceName = config.GetService(serviceId) is { } known ? Name(known) : $"Service '{serviceId}'";
        string? profileId = null;

        using var stepScope = logger.BeginScope(new LogScope(("Step", step.Alias), ("ServiceId", serviceId)));

        // Dependencies: a failed upstream fails this step; a skipped upstream skips it.
        foreach (var dep in plan.DependsOn[step.Alias])
        {
            var upstream = previous[dep];
            if (upstream.Failed)
            {
                var reason = $"Not run because '{dep}' failed.";
                logger.StepSkipped(step.Alias, reason);
                return StepRun.Fail(step, new StepFailure(DownstreamErrorCode.DependencyFailed, reason, null, null, 0), serviceId, mode, clock) with { Cascaded = true };
            }
            if (upstream.Trace.Outcome == StepOutcome.Skipped) return Skip(step, $"Not run because '{dep}' was skipped.", clock);
        }

        try
        {
            if (step.When is not null && !Conditions.IsTrue(step.When, scope))
                return Skip(step, "Condition not met.", clock);

            var endpoint = config.FindEndpoint(step.Endpoint, out var service)
                ?? throw new BindingException($"Endpoint '{step.Endpoint}' no longer exists.");
            var profile = config.GetAuthProfile(endpoint.AuthProfileId ?? service!.AuthProfileId);
            profileId = profile?.Id;
            var call = new CallTarget(step, service!, endpoint, profile);

            StepRun run;
            if (step.ForEach is null)
            {
                var single = await CallAsync(call, scope, caller, mode, token);
                run = StepRun.Ok(step, single.Output, single.Status, clock, 1, single.FromCache, single.Request, serviceId);
            }
            else
            {
                // forEach: one call per (unique) item, bounded concurrency, output is an array in first-seen item order.
                var items = Template.Resolve(step.ForEach, scope) as JsonArray ?? [];
                // Call each unique value once (the limit below then counts unique items).
                if (step.Distinct) items = (JsonArray)Transforms.Apply(new TransformCall("distinct", null), items)!;
                if (items.Count > _options.MaxForEachItems)
                    throw new BindingException($"For-each has {items.Count} items; the limit is {_options.MaxForEachItems}.");

                using var gate = new SemaphoreSlim(step.MaxConcurrency);
                var tasks = items.Select(async (item, index) =>
                {
                    await gate.WaitAsync(token);
                    try { return await CallAsync(call, scope.With("item", item, "index", index), caller, mode, token); }
                    finally { gate.Release(); }
                }).ToList();
                var outputs = await Task.WhenAll(tasks);

                run = StepRun.Ok(step, new JsonArray(outputs.Select(o => (JsonNode?)o.Output).ToArray()),
                    outputs.Length > 0 ? outputs.Max(o => o.Status) : 200, clock, outputs.Length, outputs.All(o => o.FromCache), outputs.FirstOrDefault()?.Request, serviceId);
            }

            logger.StepSucceeded(step.Alias, serviceId, endpointId, run.Trace.HttpStatus, run.Trace.DurationMs, run.Trace.Calls, run.Trace.FromCache);
            return run;
        }
        catch (Exception ex) when (IsStepFailure(ex, callerToken))
        {
            var failure = Classify(ex, serviceName);
            if (ex is AuthFailedException) logger.DownstreamAuthFailed(profileId ?? "none", serviceId, ex.Message);
            // The internal detail is always logged; whether the trace shows it depends on the mode.
            logger.StepFailed(step.Alias, serviceId, endpointId, failure.Code, failure.Status, clock.ElapsedMilliseconds, failure.Detail ?? failure.Message);
            return StepRun.Fail(step, failure, serviceId, mode, clock);
        }
    }

    private StepRun Skip(StepDef step, string reason, Stopwatch clock)
    {
        logger.StepSkipped(step.Alias, reason);
        return StepRun.Skip(step, reason, clock);
    }

    /// <summary>The exceptions that mean "this step failed". Anything else is a bug and is allowed to surface.</summary>
    private static bool IsStepFailure(Exception ex, CancellationToken callerToken) => ex switch
    {
        DownstreamException or BindingException or AuthFailedException or FormatException or HttpRequestException => true,
        // A cancellation we didn't ask for is a step or recipe timeout. If the caller hung up, let it propagate.
        OperationCanceledException => !callerToken.IsCancellationRequested,
        _ => false
    };

    /// <summary>
    /// Turns an exception into a stable code, a SAFE message for the caller, and an internal detail.
    /// Messages never include secret values, URLs or downstream bodies.
    /// </summary>
    private static StepFailure Classify(Exception ex, string serviceName) => ex switch
    {
        DownstreamException d => new StepFailure(d.Code, d.Message, d.Status, d.Detail, 1),
        BindingException or FormatException => new StepFailure(DownstreamErrorCode.MissingValue, ex.Message, null, null, 0),
        AuthFailedException => new StepFailure(DownstreamErrorCode.AuthFailed, $"The gateway couldn't sign in to {serviceName}.", null, ex.Message, 0),
        HttpRequestException h => new StepFailure(DownstreamErrorCode.Unreachable, $"{serviceName} couldn't be reached.", null, $"Connection problem: {h.HttpRequestError}.", 1),
        OperationCanceledException => new StepFailure(DownstreamErrorCode.Timeout, $"{serviceName} didn't answer in time.", null, null, 1),
        _ => throw new InvalidOperationException("Not a step failure.") // IsStepFailure keeps us from getting here
    };

    private async Task<CallResult> CallAsync(CallTarget call, IValueScope scope, CallerContext caller, ExecutionMode mode, CancellationToken token)
    {
        var built = RequestBuilder.Build(call.Service, call.Endpoint, call.Step.Bind, scope);
        var described = mode.IncludeRequestDetails ? built.Describe() : null;

        string? stepKey = null;
        if (mode.UseCache && call.Step.Cache.Enabled)
        {
            stepKey = CacheKeys.Step(call.Service.Id, call.Endpoint.Id, CacheKeys.CallerPart(call.Step.Cache.PerCaller, caller.Subject), built.Fingerprint());
            if (cache.TryGet(stepKey, out var hit))
            {
                logger.ResponseCacheHit("step", call.Step.Alias);
                return new CallResult(JsonNode.Parse(hit)!.AsObject(), 200, true, described);
            }
        }

        using var stepTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        stepTimeout.CancelAfter(call.Step.TimeoutMs ?? call.Service.TimeoutMs);
        var client = httpClientFactory.CreateClient(EngineHttpClients.Downstream);

        for (var attempt = 0; ; attempt++)
        {
            using var message = built.ToHttpRequestMessage();
            message.Headers.TryAddWithoutValidation("X-Correlation-Id", caller.CorrelationId);
            await authenticator.ApplyAsync(call.Profile, message, caller, stepTimeout.Token);

            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stepTimeout.Token);

            // An expired session or token: forget it, sign in again, retry once.
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && HasCachedCredentials(call.Profile))
            {
                authenticator.Invalidate(call.Profile, "service answered 401");
                continue;
            }

            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new DownstreamException(DownstreamErrorCode.ForStatus(status), $"{Name(call.Service)} answered {status}.", status);

            var output = new JsonObject
            {
                ["status"] = status,
                ["headers"] = ReadHeaders(response),
                ["body"] = await ReadBodyAsync(response, Name(call.Service), stepTimeout.Token)
            };

            if (stepKey is not null)
                cache.Set(stepKey, Encoding.UTF8.GetBytes(output.ToJsonString()), TimeSpan.FromSeconds(call.Step.Cache.TtlSeconds));

            return new CallResult(output, status, false, described);
        }
    }

    private async Task<JsonNode?> ReadBodyAsync(HttpResponseMessage response, string serviceName, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
            throw new DownstreamException(DownstreamErrorCode.InvalidResponse, $"The response from {serviceName} was too large.", (int)response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > _options.MaxResponseBytes)
                throw new DownstreamException(DownstreamErrorCode.InvalidResponse, $"The response from {serviceName} was too large.", (int)response.StatusCode);
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try { return JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)); }
            catch (JsonException) { throw new DownstreamException(DownstreamErrorCode.InvalidResponse, $"{serviceName} returned an invalid response.", (int)response.StatusCode); }
        }
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            ? JsonValue.Create(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length))
            : null; // binary content is not aggregated
    }

    private static JsonObject ReadHeaders(HttpResponseMessage response)
    {
        var headers = new JsonObject();
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue; // never surface downstream sessions
            headers[name] = string.Join(", ", values);
        }
        return headers;
    }

    private static bool HasCachedCredentials(AuthProfile? profile) =>
        profile?.Steps.Any(s => s.Kind is "SessionLogin" or "OAuthClientCredentials") == true;

    /// <summary>No response template: one property per step, then one per combined list (so combines are never invisible).</summary>
    private static JsonNode DefaultResponse(Recipe recipe, IReadOnlyDictionary<string, StepRun> runs, List<CombineRun> combines)
    {
        var body = new JsonObject();
        foreach (var step in recipe.Steps)
            body[step.Alias] = runs[step.Alias].Output is JsonObject o ? o["body"]?.DeepClone() : runs[step.Alias].Output?.DeepClone();
        foreach (var combine in combines)
            body[combine.Name] = combine.Records?.DeepClone();
        return body;
    }

    private static string Name(ApiService service) => string.IsNullOrEmpty(service.Name) ? service.Id : service.Name;

    // ------------------------------------------------------------------ small types

    private sealed record CallTarget(StepDef Step, ApiService Service, ApiEndpoint Endpoint, AuthProfile? Profile);

    /// <summary>The outcome of one combined list. Records is null when it failed.</summary>
    private sealed record CombineRun(string Name, bool Required, JsonArray? Records, StepTrace Trace)
    {
        public bool Failed => Records is null;
    }

    private sealed record CallResult(JsonObject Output, int Status, bool FromCache, string? Request);

    /// <summary>A downstream call that came back unusable. Message is caller-safe; Detail is internal.</summary>
    private sealed class DownstreamException(string code, string message, int status, string? detail = null) : Exception(message)
    {
        public string Code { get; } = code;
        public int Status { get; } = status;
        public string? Detail { get; } = detail;
    }

    /// <summary>Why a step failed, before it is turned into a trace.</summary>
    private sealed record StepFailure(string Code, string Message, int? Status, string? Detail, int Calls);

    private sealed record StepRun(StepDef Step, JsonNode? Output, StepTrace Trace)
    {
        public bool Failed => Trace.Outcome == StepOutcome.Failed;

        /// <summary>Failed only because an upstream step failed.</summary>
        public bool Cascaded { get; init; }

        public static StepRun Ok(StepDef s, JsonNode? output, int status, Stopwatch clock, int calls, bool fromCache, string? request, string serviceId) =>
            new(s, output, new StepTrace(s.Alias, s.DisplayName, StepOutcome.Succeeded, status, clock.ElapsedMilliseconds, calls, fromCache, null, request, ServiceId: serviceId));

        /// <summary>The internal Detail only goes into the trace for admin test runs.</summary>
        public static StepRun Fail(StepDef s, StepFailure f, string serviceId, ExecutionMode mode, Stopwatch clock) =>
            new(s, null, new StepTrace(s.Alias, s.DisplayName, StepOutcome.Failed, f.Status, clock.ElapsedMilliseconds, f.Calls, false, f.Message,
                ErrorCode: f.Code, ServiceId: serviceId, Retryable: DownstreamErrorCode.Retryable(f.Code), Detail: mode.IncludeRequestDetails ? f.Detail : null));

        public static StepRun Skip(StepDef s, string message, Stopwatch clock) =>
            new(s, null, new StepTrace(s.Alias, s.DisplayName, StepOutcome.Skipped, null, clock.ElapsedMilliseconds, 0, false, message));
    }
}
