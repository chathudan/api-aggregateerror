// aggregator - validate and run recipes from the command line.
//
//   aggregator validate [--config ./config]                       validate every recipe, auth profile and browser origin
//                                                                 (exit code 1 on problems; use in CI)
//   aggregator schema                                             print the JSON Schema of a recipe (docs/schemas/recipe.schema.json)
//   aggregator run <recipeId> [--route id=C-10042] [--query status=open] [--header X-Tenant=t1]
//                  [--sub test-user] [--token <caller's token>] [--repeat 3] [--config ./config]
//
// The sample data used by the guides is in samples/config:  aggregator run people-directory --config samples/config
//
// Secrets for "secret:" references come from environment variables, e.g.
//   export Secrets__service-a-password=pass-a

using System.Diagnostics;
using System.Text.Json;
using ApiAggregator.Engine;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: aggregator validate [--config dir] | aggregator schema | aggregator run <recipeId> [--route k=v] [--query k=v] [--header k=v] [--sub user] [--token caller-token] [--repeat n]");
    return 0;
}

// The recipe JSON Schema (ADR 0019): no configuration needed.
if (args[0] == "schema")
{
    Console.WriteLine(ApiAggregator.Engine.Authoring.RecipeSchema.Json);
    return 0;
}

var options = Options.Parse(args);
var builder = Host.CreateApplicationBuilder();
builder.Configuration["Aggregator:Config:Directory"] = options.ConfigDir;
builder.Logging.ClearProviders().AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
builder.Services.AddAggregatorEngine(builder.Configuration);
using var host = builder.Build();

var store = host.Services.GetRequiredService<IConfigStore>();
ConfigSnapshot snapshot;
try { snapshot = await store.GetSnapshotAsync(); }
catch (ConfigException problem)
{
    Console.WriteLine($"FAIL {problem.Message}");
    return 1;
}

switch (args[0])
{
    case "validate":
    {
        var failed = 0;
        foreach (var recipe in snapshot.Recipes.OrderBy(r => r.Id))
        {
            var result = RecipeValidator.Validate(recipe, snapshot);
            Console.WriteLine($"{(result.IsValid ? "OK  " : "FAIL")} {recipe.Id} v{recipe.Version} ({recipe.Method} /bff/{recipe.Route})");
            foreach (var issue in result.Issues) Console.WriteLine($"       {issue.Where}: {issue.Message}");
            if (!result.IsValid) failed++;
        }
        foreach (var (where, message) in SignInReferences.All(snapshot)) { Console.WriteLine($"FAIL {where}: {message}"); failed++; }
        // Certificate settings (ADR 0018). "Accept any certificate" is a note here, not a failure: the files may be for
        // Development, where it's allowed. The admin app refuses it elsewhere, and the gateway ignores it there.
        foreach (var service in snapshot.Services.OrderBy(s => s.Id))
        {
            foreach (var problem in CertificateRules.Problems(service, isDevelopment: true)) { Console.WriteLine($"FAIL services.{service.Id}: {problem}"); failed++; }
            if (service.Certificates?.AcceptAny == true)
                Console.WriteLine($"NOTE services.{service.Id}: accepts any certificate. Honoured only in Development; elsewhere its certificate is checked as usual.");
        }
        var auth = host.Services.GetRequiredService<DownstreamAuthenticator>();
        foreach (var profile in snapshot.AuthProfiles)
            foreach (var problem in auth.Check(profile)) { Console.WriteLine($"FAIL auth-profile {profile.Id}: {problem}"); failed++; }
        // Browser origins added in the admin app (cors.json, ADR 0015). The form only: the limits (patterns, plain http)
        // are host settings, which the gateway checks again when it loads them.
        foreach (var entry in snapshot.BrowserOrigins)
        {
            var check = OriginRules.CheckOrigin(entry.Origin, isDevelopment: true);
            var problem = check.Problem ?? (check.Value != entry.Origin ? $"write it as {check.Value}, the form browsers send." : null)
                ?? OriginRules.CheckNote(entry.Note);
            if (problem is not null) { Console.WriteLine($"FAIL cors.json origin {entry.Origin}: {problem}"); failed++; }
        }
        Console.WriteLine(failed == 0 ? "All good." : $"{failed} problem(s).");
        return failed == 0 ? 0 : 1;
    }

    case "run" when args.Length > 1:
    {
        var recipe = snapshot.GetRecipe(args[1]);
        if (recipe is null) { Console.Error.WriteLine($"Recipe '{args[1]}' not found in {options.ConfigDir}."); return 2; }

        var validation = RecipeValidator.Validate(recipe, snapshot);
        if (!validation.IsValid)
        {
            foreach (var issue in validation.Issues) Console.Error.WriteLine($"{issue.Where}: {issue.Message}");
            return 1;
        }

        var executor = host.Services.GetRequiredService<CompositionExecutor>();
        // --token plays the app's own sign-in, for endpoints whose sign-in passes the caller's token on.
        var caller = new CallerContext { Subject = options.Subject, BearerToken = options.Token };
        var inbound = new InboundRequest { Route = options.Route, Query = options.Query, Headers = options.Headers };
        var mode = new ExecutionMode { UseCache = true, IncludeRequestDetails = true };

        for (var i = 1; i <= options.Repeat; i++)
        {
            var clock = Stopwatch.StartNew();
            var result = await executor.ExecuteAsync(recipe, inbound, caller, mode, CancellationToken.None);
            Console.WriteLine($"--- run {i}: HTTP {result.StatusCode} in {clock.ElapsedMilliseconds} ms{(result.FromCache ? " (from cache)" : "")}{(result.Partial ? " (partial)" : "")}");
            foreach (var t in result.Trace)
                Console.WriteLine($"    {t.Outcome,-9} {t.Alias,-10} {t.HttpStatus?.ToString() ?? "-",-4} {t.DurationMs,5} ms  calls={t.Calls}{(t.FromCache ? " cached" : "")}  {t.Request}{(t.Message is null ? "" : "  " + t.Message)}{(t.ErrorCode is null ? "" : $" [{t.ErrorCode}]")}{(t.Detail is null ? "" : " — " + t.Detail)}");
            if (i == options.Repeat) Console.WriteLine(result.Body?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'. Try --help.");
        return 2;
}

sealed class Options
{
    public string ConfigDir { get; private set; } = "config";
    public string? Subject { get; private set; } = "cli-user";
    public string? Token { get; private set; }
    public int Repeat { get; private set; } = 1;
    public Dictionary<string, string?> Route { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> Query { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length - 1; i++)
        {
            var value = args[i + 1];
            switch (args[i])
            {
                case "--config": o.ConfigDir = value; break;
                case "--sub": o.Subject = value; break;
                case "--token": o.Token = value; break;
                case "--repeat": o.Repeat = Math.Max(1, int.Parse(value)); break;
                case "--route": Put(o.Route, value); break;
                case "--query": Put(o.Query, value); break;
                case "--header": Put(o.Headers, value); break;
            }
        }
        return o;
    }

    private static void Put(Dictionary<string, string?> target, string pair)
    {
        var eq = pair.IndexOf('=');
        if (eq > 0) target[pair[..eq]] = pair[(eq + 1)..];
    }
}
