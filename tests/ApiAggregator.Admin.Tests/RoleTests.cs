using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ApiAggregator.Admin.Application;
using ApiAggregator.Admin.Hosting;
using ApiAggregator.Admin.Services;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;
using ApiAggregator.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace ApiAggregator.Admin.Tests;

/// <summary>
/// Roles are enforced inside AdminService, so the pages (which call it directly) and /admin/* follow the same rules;
/// and history records the person who signed in.
/// </summary>
public sealed class RoleTests(AdminApp app) : IClassFixture<AdminApp>
{
    private static readonly AdminActor Author = new("author-1", "Ann Author", IsAuthor: true, IsPublisher: false);
    private static readonly AdminActor Publisher = new("publisher-1", "Pat Publisher", IsAuthor: true, IsPublisher: true);

    private AdminService Service => app.Services.GetRequiredService<AdminService>();

    [Fact]
    public async Task An_author_is_refused_publishing_both_from_a_page_and_from_the_admin_api()
    {
        await TestedDraft("roles-publish");

        // A page: it calls AdminService with the signed-in person.
        var fromPage = await Service.PublishAsync(Author, "roles-publish");
        Assert.Equal(FailureKind.Forbidden, fromPage.Failure?.Kind);

        // A script: the same action through /admin/* with an author's token.
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token("aggregator.author", subject: "author-1"));
        var fromApi = await client.PostAsync("/admin/recipes/roles-publish/publish", null);
        Assert.Equal(HttpStatusCode.Forbidden, fromApi.StatusCode);

        // Neither published it.
        Assert.Null((await app.Services.GetRequiredService<IConfigStore>().GetSnapshotAsync()).GetRecipe("roles-publish"));
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("delete")]
    public async Task An_author_is_refused_the_other_publisher_actions(string action)
    {
        var result = action == "rollback"
            ? (await Service.RollBackAsync(Author, "anything", 1)).Failure
            : (await Service.DeleteEndpointAsync(Author, "anything")).Failure;

        Assert.Equal(FailureKind.Forbidden, result?.Kind);
    }

    [Fact]
    public async Task A_service_naming_a_sign_in_that_does_not_exist_is_not_saved()
    {
        var service = new ApiService
        {
            Id = "no-such-sign-in", Name = "No such sign-in", BaseUrl = "http://localhost:5100/x", AuthProfileId = "missing-signin",
            Endpoints = [new ApiEndpoint { Id = "get", Path = "/x" }]
        };

        var result = await Service.SaveServiceAsync(Author, service.Id, service);

        Assert.Equal(FailureKind.BadRequest, result.Failure?.Kind);
        Assert.Equal("The sign-in 'missing-signin' doesn't exist. Save the sign-in first, or pick another one.", result.Failure?.Message);
        Assert.Null((await app.Services.GetRequiredService<IConfigStore>().GetSnapshotAsync()).GetService(service.Id));

        // An endpoint's own sign-in is checked the same way.
        var endpointOnly = new ApiService
        {
            Id = service.Id, Name = service.Name, BaseUrl = service.BaseUrl,
            Endpoints = [new ApiEndpoint { Id = "get", Path = "/x", AuthProfileId = "missing-signin" }]
        };
        Assert.Equal(FailureKind.BadRequest, (await Service.SaveServiceAsync(Author, service.Id, endpointOnly)).Failure?.Kind);
    }

    // ---------------------------------------------------------------- service certificates (ADR 0018)

    private static ApiService SelfSigned(string id, ServiceCertificates certificates, string baseUrl = "https://selfsigned.test/api") => new()
    {
        Id = id, Name = "Self-signed", BaseUrl = baseUrl, Certificates = certificates,
        Endpoints = [new ApiEndpoint { Id = "get", Path = "/x" }]
    };

    [Fact]
    public async Task A_service_trusting_its_own_certificate_by_fingerprint_is_saved_and_kept()
    {
        var fingerprint = string.Join(':', Enumerable.Repeat("3F", 32));
        var result = await Service.SaveServiceAsync(Author, "pinned", SelfSigned("pinned", new ServiceCertificates { Trusted = [fingerprint] }));

        Assert.True(result.Ok, result.Failure?.Message);
        var saved = (await app.Services.GetRequiredService<IConfigStore>().GetSnapshotAsync()).GetService("pinned");
        Assert.Equal([fingerprint], saved?.Certificates?.Trusted);
    }

    [Fact]
    public void Renaming_a_service_or_changing_its_endpoints_keeps_its_certificate_settings()
    {
        var service = SelfSigned("keep", new ServiceCertificates { Trusted = [new string('B', 64)] });

        Assert.Same(service.Certificates, service.With(name: "New name").Certificates);
        Assert.Same(service.Certificates, service.With(endpoints: []).Certificates);
        Assert.Null(service.With(clearCertificates: true).Certificates);
    }

    [Fact]
    public async Task Accepting_any_certificate_is_refused_outside_development()
    {
        // AdminApp runs as Production.
        var result = await Service.SaveServiceAsync(Author, "any-cert", SelfSigned("any-cert", new ServiceCertificates { AcceptAny = true }));

        Assert.Equal(FailureKind.BadRequest, result.Failure?.Kind);
        Assert.StartsWith("Accepting any certificate is allowed only in Development.", result.Failure?.Message);
        Assert.Null((await app.Services.GetRequiredService<IConfigStore>().GetSnapshotAsync()).GetService("any-cert"));
        Assert.False(Service.GetCapabilities(Author).Value?.CanAcceptAnyCertificate);
    }

    [Fact]
    public async Task A_fingerprint_that_is_not_one_is_refused_with_how_to_get_it()
    {
        var result = await Service.SaveServiceAsync(Author, "bad-pin", SelfSigned("bad-pin", new ServiceCertificates { Trusted = ["copy-paste-mistake"] }));

        Assert.Equal(FailureKind.BadRequest, result.Failure?.Kind);
        Assert.Equal("'copy-paste-mistake' isn't a SHA-256 fingerprint. Use Read certificate to get it: 64 characters, 0-9 and A-F (colons are fine).", result.Failure?.Message);
    }

    [Fact]
    public async Task Reading_a_certificate_needs_the_author_role_and_an_https_service()
    {
        var nobody = new AdminActor("nobody", null, IsAuthor: false, IsPublisher: false);
        Assert.Equal(FailureKind.Forbidden, (await Service.ReadServiceCertificateAsync(nobody, "people")).Failure?.Kind);

        // people.test is plain http: nothing is connected to.
        var plain = await Service.ReadServiceCertificateAsync(Author, "people");
        Assert.Equal(FailureKind.BadRequest, plain.Failure?.Kind);
        Assert.Equal("people uses a plain http address, which has no certificate. Certificates only apply to https.", plain.Failure?.Message);

        // Through /admin/* the same way, and only for a saved service (it can't be pointed at any address).
        var client = app.ClientWithToken("aggregator.author");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/services/no-such-service/certificate")).StatusCode);
    }

    [Fact]
    public async Task Reading_a_self_signed_certificate_shows_its_fingerprint_and_that_trusting_it_counts()
    {
        var server = new SelfSignedServer();
        await server.InitializeAsync();
        try
        {
            var display = CertificateRules.Display(server.Fingerprint);
            await Service.SaveServiceAsync(Author, "read-cert", SelfSigned("read-cert", new ServiceCertificates(), server.Url));

            var first = (await Service.ReadServiceCertificateAsync(Author, "read-cert")).Value!;
            Assert.Equal("CN=localhost", first.Subject);
            Assert.Equal(display, first.Fingerprint);
            Assert.False(first.TrustedByMachine);
            Assert.False(first.TrustedByService);
            Assert.Equal("No authority this machine trusts signed it (a test server's own certificate looks like this).", first.Problem);

            await Service.SaveServiceAsync(Author, "read-cert", SelfSigned("read-cert", new ServiceCertificates { Trusted = [display] }, server.Url));
            Assert.True((await Service.ReadServiceCertificateAsync(Author, "read-cert")).Value!.TrustedByService);
        }
        finally { await server.DisposeAsync(); }
    }

    private static CallerContext Caller() => CallerContext.Anonymous("try-1");

    private static EndpointTry Try(string path, Dictionary<string, string?>? values = null, ParamDef[]? parameters = null) =>
        new(new ApiEndpoint { Id = "tryPeople", Summary = "People (trying)", Path = path, Parameters = (parameters ?? []).ToList() }, values, null);

    [Fact]
    public async Task An_author_sends_a_test_call_to_an_endpoint_that_is_not_saved_and_nothing_is_saved()
    {
        var result = await Service.TryServiceEndpointAsync(Author, "people",
            Try("/people", new() { ["query.status"] = "active" }, [new ParamDef { Name = "status", In = "query" }]), Caller());

        Assert.True(result.Ok, result.Failure?.Message);
        Assert.True(result.Value!.Passed);
        var call = Assert.Single(Assert.Single(result.Value.Trace!).Details!);
        Assert.Equal("GET http://people.test/api/people?status=active", call.Request);
        Assert.NotNull(result.Value.Body?["call"]);

        var people = (await app.Services.GetRequiredService<IConfigStore>().GetSnapshotAsync()).GetService("people")!;
        Assert.DoesNotContain(people.Endpoints, e => e.Id == "tryPeople");
    }

    [Fact]
    public async Task A_failed_test_call_shows_what_the_service_answered()
    {
        var result = await Service.TryServiceEndpointAsync(Author, "people", Try("/missing"), Caller());

        Assert.False(result.Value!.Passed);
        Assert.Equal(404, Assert.Single(Assert.Single(result.Value.Trace!).Details!).Status);
    }

    [Fact]
    public async Task Test_values_are_checked_like_any_endpoint()
    {
        var missing = await Service.TryServiceEndpointAsync(Author, "people", Try("/people/{personId}"), Caller());
        Assert.Contains(missing.Value!.Issues!, i => i.Message.Contains("'personId' (path) is required"));

        var authorization = await Service.TryServiceEndpointAsync(Author, "people", Try("/people", new() { ["header.Authorization"] = "Bearer x" }), Caller());
        Assert.Contains(authorization.Value!.Issues!, i => i.Message.Contains("can't be set here"));

        var expression = await Service.TryServiceEndpointAsync(Author, "people", Try("/people/{personId}", new() { ["path.personId"] = "{{request.claims.sub}}" }), Caller());
        Assert.Equal(FailureKind.BadRequest, expression.Failure?.Kind);
    }

    [Fact]
    public async Task A_test_call_needs_the_author_role_and_a_service_that_exists()
    {
        var nobody = new AdminActor("nobody-1", "No Role", IsAuthor: false, IsPublisher: false);
        Assert.Equal(FailureKind.Forbidden, (await Service.TryServiceEndpointAsync(nobody, "people", Try("/people"), Caller())).Failure?.Kind);
        Assert.Equal(FailureKind.NotFound, (await Service.TryServiceEndpointAsync(Author, "no-such-service", Try("/people"), Caller())).Failure?.Kind);
    }

    [Fact]
    public async Task Someone_without_a_role_can_do_nothing_at_all()
    {
        var nobody = AdminActor.From(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "x")], "test")));

        Assert.False(nobody.IsAuthor);
        Assert.Equal(FailureKind.Forbidden, (await Service.GetServicesAsync(nobody)).Failure?.Kind);
        Assert.Equal(FailureKind.Forbidden, Service.GetCapabilities(nobody).Failure?.Kind);
    }

    [Fact]
    public async Task History_records_the_signed_in_person_with_their_name()
    {
        // The principal the development sign-in creates; OpenID Connect gives the same claims (sub, name, role).
        var person = AdminActor.From(DevSignIn.Principal("Dana Tester", "publisher"));
        await TestedDraft("roles-history", person);

        Assert.True((await Service.PublishAsync(person, "roles-history")).Ok);

        var entry = Assert.Single((await Service.GetHistoryAsync(person, "roles-history")).Value!);
        Assert.Equal("dev:dana-tester", entry.By);
        Assert.Equal("Dana Tester", entry.ByName);

        // Written to the history file too (append-only, ADR 0012).
        var file = await File.ReadAllTextAsync(Path.Combine(app.ConfigDir, "history", "roles-history.v1.json"));
        Assert.Equal("Dana Tester", JsonDocument.Parse(file).RootElement.GetProperty("byName").GetString());
    }

    [Fact]
    public async Task History_records_the_token_subject_for_admin_api_calls()
    {
        await TestedDraft("roles-api-history");
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token("aggregator.publisher", subject: "script-7"));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/admin/recipes/roles-api-history/publish", null)).StatusCode);

        var history = await client.GetFromJsonAsync<JsonElement>("/admin/recipes/roles-api-history/history");
        Assert.Equal("script-7", history[0].GetProperty("by").GetString());
    }

    [Fact]
    public void Roles_come_from_token_scopes_or_role_claims_and_a_publisher_is_an_author()
    {
        var fromScope = AdminActor.From(Principal(new Claim("scope", "customer.read aggregator.publisher")));
        var fromRoles = AdminActor.From(Principal(new Claim("roles", "aggregator.author")));

        Assert.True(fromScope.IsPublisher);
        Assert.True(fromScope.IsAuthor);
        Assert.True(fromRoles.IsAuthor);
        Assert.False(fromRoles.IsPublisher);
        Assert.False(AdminActor.From(new ClaimsPrincipal(new ClaimsIdentity())).IsAuthor); // not signed in
    }

    // ------------------------------------------------------------------ helpers

    private static ClaimsPrincipal Principal(Claim role) => new(new ClaimsIdentity([new Claim("sub", "u-1"), role], "test"));

    /// <summary>A draft that has passed Try it, ready to publish.</summary>
    private async Task TestedDraft(string id, AdminActor? who = null)
    {
        who ??= Publisher;
        var recipe = JsonSerializer.Deserialize<Recipe>($$$"""
            { "id": "{{{id}}}", "name": "Role test", "method": "GET", "route": "{{{id}}}",
              "steps": [ { "alias": "crm", "endpoint": "people/listPeople" } ],
              "response": { "people": "{{crm.body.items}}" } }
            """, AggregatorJson.Options)!;
        Assert.True((await Service.SaveDraftAsync(who, id, recipe)).Ok);
        var test = await Service.TestAsync(who, id, new TestRequest(null, null, null), CallerContext.Anonymous());
        Assert.True(test.Value?.Passed, "The draft should pass Try it against the stubbed service.");
    }
}
