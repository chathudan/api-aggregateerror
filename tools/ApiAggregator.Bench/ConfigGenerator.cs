using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;

namespace ApiAggregator.Bench;

/// <summary>One generated endpoint, as the load runner needs it: how to call it and which services it uses.</summary>
public sealed record BenchEndpoint(string Id, string Method, string Route, bool Anonymous, string[] Services, string[] RequiredServices, bool Cached, string Shape);

/// <summary>
/// Writes a config folder with many services and endpoints that all point at the mocks' /bench/sNN services. Repeatable:
/// the same counts and seed give the same files. Every endpoint passes the same checks as the admin app (RecipeValidator).
/// </summary>
public static class ConfigGenerator
{
    public const string Scope = "bench.read";
    public const string ManifestFile = "bench-manifest.json";

    private static readonly string[] Areas =
    [
        "orders", "patients", "billing", "catalog", "accounts", "devices", "visits", "claims", "stock", "staff",
        "rosters", "labs", "payments", "messages", "sites", "plans", "tickets", "assets", "reports", "shipments"
    ];

    public static List<BenchEndpoint> Generate(string dir, int recipes, int services, string mocksUrl, int seed = 42)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(Path.Combine(dir, "recipes"));
        Directory.CreateDirectory(Path.Combine(dir, "drafts"));
        Directory.CreateDirectory(Path.Combine(dir, "history"));

        var serviceList = Enumerable.Range(1, services).Select(n => Service(n, mocksUrl.TrimEnd('/'))).ToList();
        var profiles = new List<AuthProfile>
        {
            new() { Id = "bench-apikey", Name = "Bench - API key", Steps = [new() { Kind = "ApiKey", Settings = new() { ["name"] = "X-Api-Key", ["value"] = "secret:bench-api-key" } }] },
            new() { Id = "bench-basic", Name = "Bench - Basic", Steps = [new() { Kind = "Basic", Settings = new() { ["username"] = "bench", ["password"] = "secret:bench-password" } }] }
        };

        var random = new Random(seed);
        var recipeList = new List<Recipe>();
        var manifest = new List<BenchEndpoint>();
        for (var i = 1; i <= recipes; i++)
        {
            var (recipe, entry) = Recipe(i, services, random);
            recipeList.Add(recipe);
            manifest.Add(entry);
        }

        // Every endpoint must be one the admin app would accept, or the benchmark measures the wrong thing.
        var snapshot = new ConfigSnapshot(serviceList, profiles, recipeList, "generated");
        foreach (var recipe in recipeList)
        {
            var check = RecipeValidator.Validate(recipe, snapshot);
            if (!check.IsValid)
                throw new InvalidOperationException($"Generated endpoint {recipe.Id} is not valid: {string.Join("; ", check.Issues.Select(x => $"{x.Where}: {x.Message}"))}");
        }

        Write(Path.Combine(dir, "services.json"), serviceList);
        Write(Path.Combine(dir, "auth-profiles.json"), profiles);
        foreach (var recipe in recipeList) Write(Path.Combine(dir, "recipes", recipe.Id + ".json"), recipe);
        // A draft for every tenth endpoint, as authors leave them: the admin app's lists read these too.
        foreach (var recipe in recipeList.Where((_, n) => n % 10 == 0))
            Write(Path.Combine(dir, "drafts", recipe.Id + ".json"), Copy(recipe, recipe.Version + 1, RecipeStatus.Draft));
        File.WriteAllText(Path.Combine(dir, ManifestFile), JsonSerializer.Serialize(manifest, AggregatorJson.Options));
        return manifest;
    }

    public static List<BenchEndpoint> ReadManifest(string dir) =>
        JsonSerializer.Deserialize<List<BenchEndpoint>>(File.ReadAllText(Path.Combine(dir, ManifestFile)), AggregatorJson.Options)
        ?? throw new InvalidOperationException($"No {ManifestFile} in {dir}. Run 'bench generate' first.");

    /// <summary>The same endpoint with a new version number: what "publish" does to the file. Used for config churn.</summary>
    public static Recipe Copy(Recipe recipe, int version, RecipeStatus status) =>
        JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(recipe, AggregatorJson.Options), AggregatorJson.Options) is { } copy
            ? new Recipe
            {
                Id = copy.Id, Name = copy.Name, Version = version, Status = status, Method = copy.Method, Route = copy.Route,
                RequireAuthentication = copy.RequireAuthentication, RequiredScope = copy.RequiredScope, Inputs = copy.Inputs,
                Steps = copy.Steps, Combine = copy.Combine, Response = copy.Response, Cache = copy.Cache, TimeoutMs = copy.TimeoutMs
            }
            : throw new InvalidOperationException("Copy failed.");

    public static void Write<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, AggregatorJson.Options) + "\n");

    public static string ServiceId(int n) => $"bench-s{n:00}";

    private static ApiService Service(int n, string mocksUrl) => new()
    {
        Id = ServiceId(n),
        Name = $"Bench service {n:00}",
        BaseUrl = $"{mocksUrl}/bench/s{n:00}",
        AuthProfileId = (n % 3) switch { 0 => "bench-apikey", 1 => "bench-basic", _ => null },
        TimeoutMs = 2000,
        Endpoints =
        [
            new() { Id = "getItem", Method = "GET", Path = "/items/{itemId}", Parameters = [new() { Name = "itemId", In = "path", Required = true, Type = "string" }] },
            new() { Id = "getDetails", Method = "GET", Path = "/items/{itemId}/details", Parameters = [new() { Name = "itemId", In = "path", Required = true, Type = "string" }] },
            new() { Id = "listItems", Method = "GET", Path = "/items", Parameters = [new() { Name = "category", In = "query", Type = "string" }, new() { Name = "n", In = "query", Type = "integer" }] },
            new() { Id = "search", Method = "POST", Path = "/search" }
        ]
    };

    private static (Recipe, BenchEndpoint) Recipe(int i, int services, Random random)
    {
        int Pick() => random.Next(1, services + 1);
        string Ep(int s, string endpoint) => $"{ServiceId(s)}/{endpoint}";
        BindingDef From(string from, string to) => new() { From = from, To = to };

        var id = $"bench-{i:0000}";
        var area = Areas[random.Next(Areas.Length)];
        // Three route forms, so the router sees literals and parameters in different places.
        var form = random.Next(10);
        var (route, inputs) = form switch
        {
            < 6 => ($"{area}/r{i:0000}/{{id}}", new[] { "route.id" }),
            < 9 => ($"{area}/{{id}}/r{i:0000}/{{sub}}", new[] { "route.id", "route.sub" }),
            _ => ($"{area}/r{i:0000}/summary", Array.Empty<string>())
        };
        var key = inputs.Length > 0 ? "request.route.id" : null;
        BindingDef Key(string to) => key is null ? new BindingDef { To = to, Template = JsonValue.Create($"I-{i:0000}") } : From(key, to);

        var a = Pick(); var b = Pick(); var c = Pick();
        var shape = (i % 5) switch { 0 => "single", 1 => "chain", 2 => "parallel", 3 => "forEach", _ => "search" };
        List<StepDef> steps;
        JsonObject response;
        string[] used, required;
        switch (shape)
        {
            case "single":
                steps = [new() { Alias = "thing", Endpoint = Ep(a, "getItem"), Bind = [Key("path.itemId")] }];
                response = new() { ["id"] = "{{thing.body.id}}", ["name"] = "{{thing.body.name}}", ["related"] = "{{thing.body.related}}" };
                used = required = [ServiceId(a)];
                break;
            case "chain":
                steps =
                [
                    new() { Alias = "thing", Endpoint = Ep(a, "getItem"), Bind = [Key("path.itemId")] },
                    new() { Alias = "details", Endpoint = Ep(b, "getDetails"), Bind = [From("thing.body.id", "path.itemId")] }
                ];
                response = new() { ["id"] = "{{thing.body.id}}", ["description"] = "{{details.body.description}}", ["tags"] = "{{details.body.tags}}" };
                used = required = [ServiceId(a), ServiceId(b)];
                break;
            case "parallel":
                steps =
                [
                    new() { Alias = "first", Endpoint = Ep(a, "getItem"), Bind = [Key("path.itemId")] },
                    new() { Alias = "second", Endpoint = Ep(b, "getItem"), Bind = [Key("path.itemId")] },
                    new() { Alias = "extra", Endpoint = Ep(c, "getDetails"), Required = false, Bind = [Key("path.itemId")] }
                ];
                response = new() { ["first"] = "{{first.body.name}}", ["second"] = "{{second.body.name}}", ["tags"] = "{{extra.body.tags}}" };
                used = [ServiceId(a), ServiceId(b), ServiceId(c)];
                required = [ServiceId(a), ServiceId(b)];
                break;
            case "forEach":
                steps =
                [
                    new() { Alias = "list", Endpoint = Ep(a, "listItems"), Bind = [Key("query.category"), new() { To = "query.n", Template = JsonValue.Create("8") }] },
                    new() { Alias = "each", Endpoint = Ep(b, "getItem"), ForEach = "{{list.body.items[*].id}}", MaxConcurrency = 4, Bind = [From("item", "path.itemId")] }
                ];
                response = new() { ["category"] = "{{list.body.category}}", ["items"] = "{{each[*].body}}" };
                used = required = [ServiceId(a), ServiceId(b)];
                break;
            default:
                steps =
                [
                    new() { Alias = "found", Endpoint = Ep(a, "search"), Bind = [Key("body.term")] },
                    new() { Alias = "top", Endpoint = Ep(b, "getItem"), Required = false, Bind = [From("found.body.items[0].id", "path.itemId")] }
                ];
                response = new() { ["total"] = "{{found.body.total}}", ["items"] = "{{found.body.items}}", ["top"] = "{{top.body.name}}" };
                used = [ServiceId(a), ServiceId(b)];
                required = [ServiceId(a)];
                break;
        }

        var anonymous = random.Next(10) == 0;
        var cached = random.Next(5) == 0;
        var recipe = new Recipe
        {
            Id = id,
            Name = $"Bench {shape} {i:0000}",
            Version = 1,
            Status = RecipeStatus.Published,
            Method = "GET",
            Route = route,
            RequireAuthentication = !anonymous,
            RequiredScope = anonymous ? null : Scope,
            Inputs = inputs.ToDictionary(n => n, _ => new InputDef { Required = true, Pattern = "^[A-Za-z0-9-]{1,40}$" }, StringComparer.OrdinalIgnoreCase),
            Steps = steps,
            Response = response,
            Cache = new CacheSettings { Enabled = cached, TtlSeconds = 30, PerCaller = true },
            TimeoutMs = 5000
        };
        return (recipe, new BenchEndpoint(id, "GET", route, anonymous, used.Distinct().ToArray(), required.Distinct().ToArray(), cached, shape));
    }
}
