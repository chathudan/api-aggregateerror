using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ApiAggregator.TestSupport;

/// <summary>
/// Inbound JWTs for host tests. The hosts run as Production (so Auth:DevSigningKey is not allowed),
/// and the tests swap in this signing key instead of an identity provider. Test code only.
/// </summary>
public static class TestAuth
{
    public const string Issuer = "aggregator-tests";
    public const string Audience = "api-aggregator";
    private const string Key = "test-only-signing-key-0123456789abcdef-0123456789";

    /// <summary>Makes the host accept tokens from <see cref="Token"/> (and nothing else).</summary>
    public static void AcceptTestTokens(IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.Authority = null;
            o.MetadataAddress = null!;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = SigningKey(Key),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        });

    /// <summary>A signed token. Pass <paramref name="signingKey"/> or <paramref name="expires"/> to make an invalid one.</summary>
    public static string Token(string scope, string subject = "test-user", string? signingKey = null, DateTime? expires = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            NotBefore = expiry.AddHours(-1),
            IssuedAt = expiry.AddHours(-1),
            Expires = expiry,
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["scope"] = scope },
            SigningCredentials = new SigningCredentials(SigningKey(signingKey ?? Key), SecurityAlgorithms.HmacSha256)
        });
    }

    private static SymmetricSecurityKey SigningKey(string key) => new(Encoding.UTF8.GetBytes(key));
}
