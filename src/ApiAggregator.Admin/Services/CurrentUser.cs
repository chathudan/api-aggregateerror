using System.Security.Claims;
using System.Text.Json.Nodes;
using ApiAggregator.Admin.Application;
using ApiAggregator.Admin.Hosting;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Hosting;
using Microsoft.AspNetCore.Components.Authorization;

namespace ApiAggregator.Admin.Services;

/// <summary>
/// The person signed in to the pages (one per browser tab). Pages pass <see cref="ActorAsync"/> to AdminService,
/// which decides what they may do.
/// </summary>
public sealed class CurrentUser(AuthenticationStateProvider authState, UserTokenStore tokens, IConfiguration configuration, IWebHostEnvironment env)
{
    private ClaimsPrincipal? _user;

    public async Task<ClaimsPrincipal> PrincipalAsync() => _user ??= (await authState.GetAuthenticationStateAsync()).User;

    public async Task<AdminActor> ActorAsync() => AdminActor.From(await PrincipalAsync());

    /// <summary>
    /// The person as the engine sees them for Try it: their id and claims, and a token for services that use the
    /// caller's own token. Development: a dev token for them. Otherwise: their access token from the sign-in.
    /// </summary>
    public async Task<CallerContext> CallerAsync()
    {
        var user = await PrincipalAsync();
        var claims = new JsonObject();
        foreach (var group in user.Claims.Where(c => c.Type != AdminAuth.SessionClaim).GroupBy(c => c.Type))
        {
            var values = group.Select(c => c.Value).ToArray();
            claims[group.Key] = values.Length == 1 ? JsonValue.Create(values[0]) : new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }
        var subject = user.FindFirst("sub")?.Value;
        return new CallerContext
        {
            Subject = subject,
            Claims = claims,
            BearerToken = await TokenAsync("customer.read people.read profile.read")
        };
    }

    /// <summary>
    /// A token for reading the gateway's live logs. Development: a dev token. Otherwise the person's access token
    /// (when the provider issues one for the gateway's audience), else the configured Gateway:LogsToken.
    /// Null when there's none: the Logs page then says what to configure.
    /// </summary>
    public async Task<string?> GatewayLogsTokenAsync() =>
        await TokenAsync(AdminActor.AuthorRole) ?? (string.IsNullOrEmpty(configuration["Gateway:LogsToken"]) ? null : configuration["Gateway:LogsToken"]);

    private async Task<string?> TokenAsync(string devScopes)
    {
        var user = await PrincipalAsync();
        if (env.IsDevelopment() && !string.IsNullOrEmpty(configuration["Auth:DevSigningKey"]))
            return InboundAuth.CreateDevToken(configuration.GetSection("Auth"), user.FindFirst("sub")?.Value ?? "dev-user", devScopes);
        return tokens.Get(user.FindFirst(AdminAuth.SessionClaim)?.Value);
    }
}
