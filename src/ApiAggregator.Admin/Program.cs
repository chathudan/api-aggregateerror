// The admin app (ADR 0014): the pages technicians use and the /admin/* HTTP API for scripts, in one process.
// Both go through AdminService, which holds every rule and role check. The gateway is a separate app.
//
//   Pages     cookie sign-in: the development sign-in (Development) or OpenID Connect (Auth:Oidc)
//   /admin/*  bearer tokens only (Auth:Authority / Auth:Audience, or /dev/token in Development)
//
// Roles: aggregator.author (services, sign-ins, drafts, Try it), aggregator.publisher (also publish, roll back, delete)
// and aggregator.admin (a separate role: browser access for the gateway, ADR 0015).

using System.Text.Json;
using ApiAggregator.Admin.Application;
using ApiAggregator.Admin.Components;
using ApiAggregator.Admin.Hosting;
using ApiAggregator.Admin.Services;
using ApiAggregator.Engine;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Logging.Live;
using ApiAggregator.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddLocalSecretsFile(); // Development only: secrets saved from the pages

// Logs: readable single lines on a developer machine, one JSON object per line (with scopes) everywhere else.
if (builder.Environment.IsDevelopment())
    builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; });
else
    builder.Logging.ClearProviders().AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    });
// Keep the latest lines in memory for the Logs page. After the console setup above: ClearProviders would remove it.
builder.Logging.AddLiveLogs(builder.Configuration.GetSection("LiveLogs"));

// ---------------------------------------------------------------- the use cases
builder.Services.AddAggregatorEngine(builder.Configuration);
builder.Services.AddSingleton<SourceCatalog>();
builder.Services.AddSingleton<OpenApiImporter>();
builder.Services.AddHttpClient(OpenApiImporter.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
    .RemoveAllLoggers(); // no default HttpClient logging: it would write full URLs (see ServiceCollectionExtensions)
builder.Services.AddSingleton<ISecretWriter, LocalSecretWriter>();
// Browser access (CORS) for the GATEWAY (ADR 0015): the same Gateway:Cors limits the gateway reads, checked at startup
// here too. This app itself answers no cross-origin requests: there is deliberately no AddCors/UseCors in it.
var browserAccess = builder.Configuration.GetSection(BrowserAccessSettings.Section).Get<BrowserAccessSettings>() ?? new BrowserAccessSettings();
OriginRules.ValidateSettings(browserAccess, builder.Environment.IsDevelopment());
builder.Services.AddSingleton(sp => new AdminOptions
{
    RequireTestBeforePublish = builder.Configuration.GetValue("Admin:RequireTestBeforePublish", true),
    CanSaveSecrets = builder.Environment.IsDevelopment(),
    IsDevelopment = builder.Environment.IsDevelopment(),
    BrowserAccess = browserAccess,
    ConfigDirectory = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfigOptions>>().Value.Directory
});
builder.Services.AddSingleton<AdminService>();

// ---------------------------------------------------------------- sign-in (two schemes, see AdminAuth)
builder.AddAdminAuthentication();

// ---------------------------------------------------------------- pages
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<UiContext>();
// The Logs page reads the gateway's live logs over HTTP ("Gateway:BaseUrl"); this app's own come from memory.
builder.Services.AddHttpClient<LiveLogClient>().RemoveAllLoggers();

builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
    o.SerializerOptions.AllowTrailingCommas = true;
});

var app = builder.Build();

// Unexpected errors: a problem-details body without the exception text, for pages and /admin/* alike. A bad request
// (for example a form posted without its antiforgery token) keeps its 400 instead of becoming a 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError
});
if (!app.Environment.IsDevelopment()) app.UseHsts();

app.UseStaticFiles();
app.UseSameOriginBlazorHub();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health/live"); // for the platform's liveness probe; says nothing but "Healthy"
app.MapAdminSignIn();
app.MapAdminApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

if (app.Environment.IsDevelopment() && !string.IsNullOrEmpty(builder.Configuration["Auth:DevSigningKey"]))
{
    // Development only: a bearer token for scripts calling /admin/* (and the gateway, which shares the dev key).
    app.MapGet("/dev/token", (string? sub, string? scope) => Results.Ok(new
    {
        access_token = InboundAuth.CreateDevToken(builder.Configuration.GetSection("Auth"), sub ?? "dev-admin", scope ?? "aggregator.author aggregator.publisher aggregator.admin customer.read")
    }));
}

app.Run();

public partial class Program;
