using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Expressions;
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
        var clock = Stopwatch.StartNew();
        var config = await store.GetSnapshotAsync(ct);

        // 1. inputs
        var (request, inputErrors) = InputBinder.Bind(recipe, inbound, caller);
        if (inputErrors.Count > 0)
            return new CompositionResult { StatusCode = 400, Body = Problem("Some inputs are missing or invalid.", inputErrors), DurationMs = clock.ElapsedMilliseconds };

        // 2. response cache
        string? responseKey = null;
        if (mode.UseCache && recipe.Cache.Enabled)
        {
            var callerKey = CacheKeys.CallerPart(recipe.Cache.PerCaller, caller.Subject);
            responseKey = CacheKeys.Response(recipe.Id, recipe.Version, config.Version, callerKey, request.ToJsonString());
            if (cache.TryGet(responseKey, out var cached))
                return new CompositionResult { StatusCode = 200, Body = JsonNode.Parse(cached), FromCache = true, DurationMs = clock.ElapsedMilliseconds };
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

        // 4/5. response
        var failures = runs.Values.Where(r => r.Failed).ToList();
        var requiredFailures = failures.Where(r => r.Step.Required).ToList();
        CompositionResult result;

        if (requiredFailures.Count > 0)
        {
            result = new CompositionResult
            {
                StatusCode = 502,
                // Report root causes only, not the steps that were skipped because of them.
                Body = Problem("A required service call failed.", failures.Where(f => !f.Cascaded).Select(Describe)),
                Trace = trace,
                DurationMs = clock.ElapsedMilliseconds
            };
        }
        else
        {
            var body = recipe.Response is not null
                ? Template.ResolveNode(recipe.Response, new Scope(roots))
                : DefaultResponse(recipe, runs);

            var partial = failures.Count > 0;
            if (partial && body is JsonObject obj)
                obj["_errors"] = new JsonArray(failures.Where(f => !f.Cascaded).Select(f => (JsonNode?)JsonValue.Create(Describe(f))).ToArray());

            result = new CompositionResult { StatusCode = 200, Body = body, Partial = partial, Trace = trace, DurationMs = clock.ElapsedMilliseconds };

            if (responseKey is not null && !partial && body is not null)
                cache.Set(responseKey, Encoding.UTF8.GetBytes(body.ToJsonString()), TimeSpan.FromSeconds(recipe.Cache.TtlSeconds));
        }

        // Audit line: who, what, outcome. Never values.
        logger.LogInformation(
            "Composition {RecipeId} v{Version} for {Subject} -> {Status} in {Duration} ms; steps {Steps}; correlation {CorrelationId}",
            recipe.Id, recipe.Version, caller.Subject ?? "anonymous", result.StatusCode, result.DurationMs,
            string.Join(",", trace.Select(t => $"{t.Alias}:{t.Outcome}")), caller.CorrelationId);

        return result;
    }

    // ------------------------------------------------------------------ steps

    private async Task<StepRun> RunStepAsync(StepDef step, ExecutionPlan plan, IReadOnlyDictionary<string, StepRun> previous,
        Scope scope, ConfigSnapshot config, CallerContext caller, ExecutionMode mode, CancellationToken token, CancellationToken callerToken)
    {
        var clock = Stopwatch.StartNew();

        // Dependencies: a failed upstream fails this step; a skipped upstream skips it.
        foreach (var dep in plan.DependsOn[step.Alias])
        {
            var upstream = previous[dep];
            if (upstream.Failed) return StepRun.Fail(step, $"Not run because '{dep}' failed.", null, clock, 0) with { Cascaded = true };
            if (upstream.Trace.Outcome == StepOutcome.Skipped) return StepRun.Skip(step, $"Not run because '{dep}' was skipped.", clock);
        }

        try
        {
            if (step.When is not null && !Conditions.IsTrue(step.When, scope))
                return StepRun.Skip(step, "Condition not met.", clock);

            var endpoint = config.FindEndpoint(step.Endpoint, out var service)
                ?? throw new BindingException($"Endpoint '{step.Endpoint}' no longer exists.");
            var profile = config.GetAuthProfile(endpoint.AuthProfileId ?? service!.AuthProfileId);
            var call = new CallTarget(step, service!, endpoint, profile);

            if (step.ForEach is null)
            {
                var single = await CallAsync(call, scope, caller, mode, token);
                return StepRun.Ok(step, single.Output, single.Status, clock, 1, single.FromCache, single.Request);
            }

            // forEach: one call per item, bounded concurrency, output is an array in item order.
            var items = Template.Resolve(step.ForEach, scope) as JsonArray ?? [];
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

            return StepRun.Ok(step, new JsonArray(outputs.Select(o => (JsonNode?)o.Output).ToArray()),
                outputs.Length > 0 ? outputs.Max(o => o.Status) : 200, clock, outputs.Length, outputs.All(o => o.FromCache), outputs.FirstOrDefault()?.Request);
        }
        catch (DownstreamException ex) { return StepRun.Fail(step, ex.Message, ex.Status, clock, 1); }
        catch (BindingException ex) { return StepRun.Fail(step, ex.Message, null, clock, 0); }
        catch (AuthFailedException ex)
        {
            logger.LogWarning("Downstream auth failed in step {Step}: {Message}", step.Alias, ex.Message);
            return StepRun.Fail(step, "Couldn't sign in to the service.", null, clock, 0);
        }
        catch (FormatException ex) { return StepRun.Fail(step, ex.Message, null, clock, 0); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return StepRun.Fail(step, "Timed out.", (int)HttpStatusCode.GatewayTimeout, clock, 1);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Step {Step} could not reach its service", step.Alias);
            return StepRun.Fail(step, "Service unreachable.", null, clock, 1);
        }
    }

    private async Task<CallResult> CallAsync(CallTarget call, IValueScope scope, CallerContext caller, ExecutionMode mode, CancellationToken token)
    {
        var built = RequestBuilder.Build(call.Service, call.Endpoint, call.Step.Bind, scope);
        var described = mode.IncludeRequestDetails ? built.Describe() : null;

        string? stepKey = null;
        if (mode.UseCache && call.Step.Cache.Enabled)
        {
            stepKey = CacheKeys.Step(call.Service.Id, call.Endpoint.Id, CacheKeys.CallerPart(call.Step.Cache.PerCaller, caller.Subject), built.Fingerprint());
            if (cache.TryGet(stepKey, out var hit))
                return new CallResult(JsonNode.Parse(hit)!.AsObject(), 200, true, described);
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
                authenticator.Invalidate(call.Profile);
                continue;
            }

            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new DownstreamException($"{Name(call.Service)} answered {status}.", status);

            var output = new JsonObject
            {
                ["status"] = status,
                ["headers"] = ReadHeaders(response),
                ["body"] = await ReadBodyAsync(response, stepTimeout.Token)
            };

            if (stepKey is not null)
                cache.Set(stepKey, Encoding.UTF8.GetBytes(output.ToJsonString()), TimeSpan.FromSeconds(call.Step.Cache.TtlSeconds));

            return new CallResult(output, status, false, described);
        }
    }

    private async Task<JsonNode?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
            throw new DownstreamException("The service response is too large.", (int)response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > _options.MaxResponseBytes)
                throw new DownstreamException("The service response is too large.", (int)response.StatusCode);
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try { return JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)); }
            catch (JsonException) { throw new DownstreamException("The service returned invalid JSON.", (int)response.StatusCode); }
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

    private static JsonNode DefaultResponse(Recipe recipe, IReadOnlyDictionary<string, StepRun> runs)
    {
        var body = new JsonObject();
        foreach (var step in recipe.Steps)
            body[step.Alias] = runs[step.Alias].Output is JsonObject o ? o["body"]?.DeepClone() : runs[step.Alias].Output?.DeepClone();
        return body;
    }

    private static string Describe(StepRun run) => $"{run.Step.DisplayName ?? run.Step.Alias}: {run.Trace.Message}";

    private static string Name(ApiService service) => string.IsNullOrEmpty(service.Name) ? service.Id : service.Name;

    private static JsonObject Problem(string title, IEnumerable<string> errors) => new()
    {
        ["title"] = title,
        ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray())
    };

    // ------------------------------------------------------------------ small types

    private sealed record CallTarget(StepDef Step, ApiService Service, ApiEndpoint Endpoint, AuthProfile? Profile);

    private sealed record CallResult(JsonObject Output, int Status, bool FromCache, string? Request);

    private sealed class DownstreamException(string message, int status) : Exception(message)
    {
        public int Status { get; } = status;
    }

    private sealed record StepRun(StepDef Step, JsonNode? Output, StepTrace Trace)
    {
        public bool Failed => Trace.Outcome == StepOutcome.Failed;

        /// <summary>Failed only because an upstream step failed.</summary>
        public bool Cascaded { get; init; }

        public static StepRun Ok(StepDef s, JsonNode? output, int status, Stopwatch clock, int calls, bool fromCache, string? request) =>
            new(s, output, new StepTrace(s.Alias, s.DisplayName, StepOutcome.Succeeded, status, clock.ElapsedMilliseconds, calls, fromCache, null, request));

        public static StepRun Fail(StepDef s, string message, int? status, Stopwatch clock, int calls) =>
            new(s, null, new StepTrace(s.Alias, s.DisplayName, StepOutcome.Failed, status, clock.ElapsedMilliseconds, calls, false, message));

        public static StepRun Skip(StepDef s, string message, Stopwatch clock) =>
            new(s, null, new StepTrace(s.Alias, s.DisplayName, StepOutcome.Skipped, null, clock.ElapsedMilliseconds, 0, false, message));
    }
}
