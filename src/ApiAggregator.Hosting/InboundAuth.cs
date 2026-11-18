using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ApiAggregator.Engine.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ApiAggregator.Hosting;

/// <summary>
/// Inbound (caller -> gateway / admin API) authentication. Shared by both hosts.
///   Production:  Auth:Authority + Auth:Audience (any OpenID Connect provider, e.g. Entra ID).
///   Development: Auth:DevSigningKey enables locally signed tokens from /dev/token.
/// </summary>
public static class InboundAuth
{
    public static void Configure(JwtBearerOptions o, IConfigurationSection auth, IWebHostEnvironment env)
    {
        o.MapInboundClaims = false; // keep "sub", "scope" as they are
        o.Authority = auth["Authority"];
        o.Audience = auth["Audience"];
        o.RequireHttpsMetadata = !env.IsDevelopment();

        var devKey = auth["DevSigningKey"];
        if (string.IsNullOrEmpty(devKey)) return;
        if (!env.IsDevelopment()) throw new InvalidOperationException("Auth:DevSigningKey must not be set outside Development.");
        if (devKey.Length < 32) throw new InvalidOperationException("Auth:DevSigningKey must be at least 32 characters.");

        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = auth["DevIssuer"] ?? "aggregator-dev",
            ValidAudience = auth["Audience"] ?? "api-aggregator",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(devKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    public static string CreateDevToken(IConfigurationSection auth, string subject, string scope) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = auth["DevIssuer"] ?? "aggregator-dev",
            Audience = auth["Audience"] ?? "api-aggregator",
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["scope"] = scope },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth["DevSigningKey"]!)), SecurityAlgorithms.HmacSha256)
        });

    /// <summary>Supports both "scope" (space separated) and "scp" claims.</summary>
    public static bool HasScope(ClaimsPrincipal user, string required) =>
        user.FindAll("scope").Concat(user.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(required, StringComparer.Ordinal);

    public static CallerContext CallerFrom(HttpContext http)
    {
        var claims = new JsonObject();
        foreach (var group in http.User.Claims.GroupBy(c => c.Type))
        {
            var values = group.Select(c => c.Value).ToArray();
            claims[group.Key] = values.Length == 1 ? JsonValue.Create(values[0]) : new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        var authorization = http.Request.Headers.Authorization.ToString();
        var incomingId = http.Request.Headers["X-Correlation-Id"].ToString();

        return new CallerContext
        {
            Subject = http.User.FindFirst("sub")?.Value,
            BearerToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : null,
            Claims = claims,
            CorrelationId = IsSafeId(incomingId) ? incomingId : Guid.NewGuid().ToString("n")
        };
    }

    private static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
