using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using ApiAggregator.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ApiAggregator.Admin.Tests;

/// <summary>
/// The two sign-in schemes (ADR 0014): the pages' cookie, set by the development sign-in, and bearer tokens for
/// /admin/*. The cookie must never work on /admin/*.
/// </summary>
public sealed partial class SignInTests : IDisposable
{
    private readonly AdminApp _dev = new() { Environment = "Development" };

    public void Dispose() => _dev.Dispose();

    [Fact]
    public async Task Signing_in_as_a_developer_sets_the_cookie_and_shows_the_name_on_the_pages()
    {
        var browser = Browser(_dev);

        var signedIn = await SignInAsync(browser, "Dana Tester", "publisher");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/endpoints", signedIn.Headers.Location?.OriginalString);

        var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Dana Tester · Publisher", await home.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Pages_send_someone_who_is_not_signed_in_to_the_sign_in_page()
    {
        var response = await Browser(_dev).GetAsync("/services");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signin", response.Headers.Location?.AbsolutePath);
        Assert.Contains("returnurl=%2fservices", response.Headers.Location?.Query.ToLowerInvariant());
    }

    [Fact]
    public async Task Signing_in_without_the_antiforgery_token_is_refused()
    {
        var response = await Browser(_dev).PostAsync("/signin/dev",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Mallory", ["role"] = "publisher" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(response.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.StartsWith("aggregator.admin")));
    }

    [Fact]
    public async Task A_sign_in_link_never_sends_people_to_another_site()
    {
        var browser = Browser(_dev);

        var response = await SignInAsync(browser, "Dana Tester", "author", returnUrl: "//evil.example/steal");

        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_mcp_server_refuses_a_request_that_carries_only_the_page_cookie()
    {
        var browser = Browser(_dev);
        await SignInAsync(browser, "Dana Tester", "publisher");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_admin_api_refuses_a_request_that_carries_only_the_page_cookie()
    {
        var browser = Browser(_dev);
        await SignInAsync(browser, "Dana Tester", "publisher");

        var withCookie = await browser.GetAsync("/admin/recipes");
        Assert.Equal(HttpStatusCode.Unauthorized, withCookie.StatusCode);

        // The same request with a bearer token works: the API itself is fine, it just never reads the cookie.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/recipes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token("aggregator.author"));
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_pages_live_connection_refuses_another_sites_origin()
    {
        var browser = Browser(_dev);
        await SignInAsync(browser, "Dana Tester", "publisher");

        HttpRequestMessage Negotiate(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
            request.Headers.Add("Origin", origin);
            return request;
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await browser.SendAsync(Negotiate("https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(Negotiate("http://localhost"))).StatusCode);
    }

    [Fact]
    public async Task Outside_development_there_is_no_development_sign_in()
    {
        using var production = new AdminApp();
        var browser = Browser(production);

        var page = await browser.GetAsync("/signin");
        var post = await browser.PostAsync("/signin/dev",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Mallory", ["role"] = "publisher" }));

        Assert.DoesNotContain("name=\"name\"", await page.Content.ReadAsStringAsync()); // no name box: it offers the organisation's sign-in
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public void Outside_development_the_admin_app_needs_openid_connect_to_start()
    {
        using var misconfigured = new AdminApp { Settings = { ["Auth:Oidc:Authority"] = "" } };

        var error = Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());
        Assert.Contains("The admin pages need a sign-in outside Development", error.ToString());
    }

    [Fact]
    public void A_session_id_from_the_identity_provider_is_replaced_so_the_persons_token_can_be_found()
    {
        // Keycloak and Entra ID put their own "sid" in the ID token.
        var identity = new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim("sub", "ann"),
            new System.Security.Claims.Claim("sid", "the-providers-session"),
            new System.Security.Claims.Claim("roles", "aggregator.author")
        ], "oidc");

        var sid = Hosting.AdminAuth.AdoptSignIn(identity, "roles");

        Assert.Equal(sid, Assert.Single(identity.FindAll("sid")).Value);
        Assert.NotEqual("the-providers-session", sid);
        Assert.Contains(identity.FindAll("role"), c => c.Value == "aggregator.author");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A client that keeps cookies and doesn't follow redirects, like a browser we can inspect.</summary>
    private static HttpClient Browser(AdminApp app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    /// <summary>The real flow: open the sign-in page, take its antiforgery token, post the form.</summary>
    private static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string name, string role, string returnUrl = "/endpoints")
    {
        var page = await (await browser.GetAsync("/signin")).Content.ReadAsStringAsync();
        var token = AntiforgeryRegex().Match(page);
        Assert.True(token.Success, "The sign-in page has no antiforgery token:\n" + page[..Math.Min(page.Length, 3000)]);

        return await browser.PostAsync("/signin/dev", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            ["name"] = name,
            ["role"] = role,
            ["returnUrl"] = returnUrl
        }));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();
}
