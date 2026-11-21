using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ApiAggregator.Admin.Application;
using ApiAggregator.Admin.Hosting;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
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
