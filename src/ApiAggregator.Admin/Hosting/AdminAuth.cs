using System.Security.Claims;
using ApiAggregator.Admin.Application;
using ApiAggregator.Hosting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;

namespace ApiAggregator.Admin.Hosting;

/// <summary>
/// Two sign-in schemes, kept strictly apart (ADR 0014):
///   Pages     a cookie, set by the development sign-in or by OpenID Connect. Sign-in and sign-out are POST forms
///             with an antiforgery token.
///   /admin/*  a bearer token (JWT) ONLY. Its policies name the bearer scheme, so the page cookie is never read
///             there: a browser can't be tricked into calling the API with it (no CSRF).
/// Roles are checked again inside AdminService; these policies are a first line only.
/// </summary>
internal static class AdminAuth
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string BearerScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string CookieName = "aggregator.admin";

    /// <summary>Policies for /admin/*: bearer tokens only.</summary>
    public const string ApiAuthor = "api-author";
    public const string ApiPublisher = "api-publisher";
    /// <summary>/admin/cors: anyone with a role may look (viewer); only the admin role may change it.</summary>
    public const string ApiViewer = "api-viewer";
    public const string ApiAdmin = "api-admin";
    public const string Mcp = "mcp";

    /// <summary>Claim holding a random id for this sign-in, so the person's access token can be kept server side.</summary>
    public const string SessionClaim = "sid";

    /// <summary>Fails startup, not the first request, when the sign-in settings can't work.</summary>
    public static void ValidateSettings(IConfiguration configuration, IHostEnvironment env)
    {
        InboundAuth.ValidateSettings(configuration.GetSection("Auth"), env);
        var oidc = configuration.GetSection("Auth:Oidc");
        if (string.IsNullOrWhiteSpace(oidc["Authority"]))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException(
                    "The admin pages need a sign-in outside Development. Set Auth:Oidc:Authority, Auth:Oidc:ClientId and " +
                    "Auth:Oidc:ClientSecret (from Key Vault or user secrets). See docs/guides/operations.md.");
            return;
        }
        if (string.IsNullOrWhiteSpace(oidc["ClientId"]))
            throw new InvalidOperationException("Auth:Oidc:Authority is set, so Auth:Oidc:ClientId must be set too.");
    }

    public static bool UsesOidc(IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration["Auth:Oidc:Authority"]);

    public static void AddAdminAuthentication(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var env = builder.Environment;
        ValidateSettings(configuration, env);
        var useOidc = UsesOidc(configuration);

        var authentication = builder.Services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieScheme;
                o.DefaultChallengeScheme = useOidc ? OidcScheme : CookieScheme;
            })
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                // Lax: sent on normal navigation, never on cross-site posts, frames or WebSocket requests.
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.LoginPath = useOidc ? "/signin/oidc" : "/signin";
                o.AccessDeniedPath = "/signin";
                o.Events.OnSigningOut = context =>
                {
                    // Forget the person's access token (OpenID Connect) with the session.
                    var sid = context.HttpContext.User.FindFirst(SessionClaim)?.Value;
                    if (sid is not null) context.HttpContext.RequestServices.GetRequiredService<UserTokenStore>().Remove(sid);
                    return Task.CompletedTask;
                };
            })
            .AddJwtBearer(BearerScheme, o => InboundAuth.Configure(o, configuration.GetSection("Auth"), env));

        if (useOidc) authentication.AddOpenIdConnect(OidcScheme, o => ConfigureOidc(o, configuration.GetSection("Auth:Oidc")));

        builder.Services.AddSingleton<UserTokenStore>();
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(ApiAuthor, p => p.AddAuthenticationSchemes(BearerScheme).RequireAuthenticatedUser()
                .RequireAssertion(c => AdminActor.From(c.User).IsAuthor))
            .AddPolicy(ApiPublisher, p => p.AddAuthenticationSchemes(BearerScheme).RequireAuthenticatedUser()
                .RequireAssertion(c => AdminActor.From(c.User).IsPublisher))
            .AddPolicy(ApiViewer, p => p.AddAuthenticationSchemes(BearerScheme).RequireAuthenticatedUser()
                .RequireAssertion(c => AdminActor.From(c.User).HasAnyRole))
            .AddPolicy(ApiAdmin, p => p.AddAuthenticationSchemes(BearerScheme).RequireAuthenticatedUser()
                .RequireAssertion(c => AdminActor.From(c.User).IsAdmin))
            // /mcp (ADR 0021): bearer only like /admin/*, so the page cookie gets 401; and the separate aggregator.mcp
            // scope (403 without it). What the agent may then do is decided by its roles, in AdminService.
            .AddPolicy(Mcp, p => p.AddAuthenticationSchemes(BearerScheme).RequireAuthenticatedUser()
                .RequireAssertion(c => AdminActor.Has(c.User, AdminActor.McpScope)));
        builder.Services.AddCascadingAuthenticationState();
    }

    /// <summary>
    /// Authorization code flow with PKCE. Tokens are NOT saved in the cookie: the access token is kept in memory on
    /// the server (<see cref="UserTokenStore"/>) for the Logs page and Try it, keyed by a random session id.
    /// </summary>
    private static void ConfigureOidc(OpenIdConnectOptions o, IConfigurationSection oidc)
    {
        o.Authority = oidc["Authority"];
        o.ClientId = oidc["ClientId"];
        o.ClientSecret = oidc["ClientSecret"];
        o.ResponseType = "code";
        o.UsePkce = true;
        o.SaveTokens = false;
        o.MapInboundClaims = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.SignInScheme = CookieScheme;
        o.Scope.Clear();
        foreach (var scope in $"openid profile {oidc["Scopes"]}".Split(' ', StringSplitOptions.RemoveEmptyEntries)) o.Scope.Add(scope);
        o.TokenValidationParameters.NameClaimType = "name";
        o.TokenValidationParameters.RoleClaimType = "role";

        var roleClaim = string.IsNullOrWhiteSpace(oidc["RoleClaim"]) ? "roles" : oidc["RoleClaim"]!;
        o.Events.OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is not ClaimsIdentity identity) return Task.CompletedTask;
            // The provider's role claim (app roles "aggregator.author" / "aggregator.publisher") becomes "role".
            foreach (var claim in identity.FindAll(roleClaim).ToList())
                if (claim.Type != "role") identity.AddClaim(new Claim("role", claim.Value));

            var sid = Guid.NewGuid().ToString("n");
            identity.AddClaim(new Claim(SessionClaim, sid));
            if (context.TokenEndpointResponse?.AccessToken is { Length: > 0 } token)
                context.HttpContext.RequestServices.GetRequiredService<UserTokenStore>()
                    .Put(sid, token, DateTimeOffset.UtcNow.AddSeconds(int.TryParse(context.TokenEndpointResponse.ExpiresIn, out var s) ? s : 3600));
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Sign-in and sign-out endpoints. The development sign-in exists only in Development; with OpenID Connect,
    /// /signin/oidc starts the provider's sign-in.
    /// </summary>
    public static void MapAdminSignIn(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/signin/dev", async ([FromForm] string? name, [FromForm] string? role, [FromForm] string? admin, [FromForm] string? returnUrl, HttpContext http) =>
            {
                var problem = DevSignIn.Problem(name, role);
                if (problem is not null)
                    return Results.Redirect($"/signin?error={Uri.EscapeDataString(problem)}&returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}");

                await http.SignInAsync(CookieScheme, DevSignIn.Principal(name!, role!, admin == "yes"));
                return Results.LocalRedirect(SafeReturnUrl(returnUrl));
            }).WithMetadata(new RequireAntiforgeryTokenAttribute()); // also implied by [FromForm]; stated so it can't be lost
        }

        if (UsesOidc(app.Configuration))
            app.MapGet("/signin/oidc", (string? returnUrl) =>
                Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [OidcScheme]));

        app.MapPost("/signout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieScheme);
            // With OpenID Connect the provider's session stays; signing out here ends the admin app's session only.
            return Results.LocalRedirect("/signin?signedOut=1");
        }).WithMetadata(new RequireAntiforgeryTokenAttribute());
    }

    /// <summary>Only addresses inside this app, so a sign-in link can't send people somewhere else.</summary>
    public static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") && !url.Any(char.IsControl)
            ? url
            : "/";

    /// <summary>
    /// The Blazor connection (/_blazor) carries the page cookie. Browsers send it only for this site (SameSite=Lax),
    /// and this refuses any connection that another site's page tries to open.
    /// </summary>
    public static IApplicationBuilder UseSameOriginBlazorHub(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments("/_blazor") && http.Request.Headers.Origin is { Count: > 0 } origin)
            {
                var self = $"{http.Request.Scheme}://{http.Request.Host}";
                if (!string.Equals(origin.ToString(), self, StringComparison.OrdinalIgnoreCase))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
            await next();
        });
}

/// <summary>The development sign-in: a name and a role, no password. Development only.</summary>
internal static class DevSignIn
{
    public const string AuthorChoice = "author";
    public const string PublisherChoice = "publisher";

    public static string? Problem(string? name, string? role)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter your name.";
        if (name.Trim().Length > 60 || name.Any(char.IsControl)) return "Use a name of up to 60 characters.";
        if (role is not (AuthorChoice or PublisherChoice)) return "Pick a role: author or publisher.";
        return null;
    }

    /// <summary>
    /// sub is "dev:" plus the name in lowercase with dashes ("Dana Tester" → dev:dana-tester), so history entries
    /// from development are easy to tell apart. A publisher is an author too. <paramref name="admin"/> adds the
    /// separate admin role (browser access settings).
    /// </summary>
    public static ClaimsPrincipal Principal(string name, string role, bool admin = false)
    {
        name = name.Trim();
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var claims = new List<Claim>
        {
            new("sub", "dev:" + (slug.Length == 0 ? "user" : slug)),
            new("name", name),
            new("role", AdminActor.AuthorRole)
        };
        if (role == PublisherChoice) claims.Add(new Claim("role", AdminActor.PublisherRole));
        if (admin) claims.Add(new Claim("role", AdminActor.AdminRole));
        claims.Add(new Claim(AdminAuth.SessionClaim, Guid.NewGuid().ToString("n")));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "dev-sign-in", "name", "role"));
    }
}
