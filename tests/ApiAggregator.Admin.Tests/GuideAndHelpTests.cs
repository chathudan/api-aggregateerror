using System.Text.Json;
using System.Text.RegularExpressions;
using ApiAggregator.Admin.Services;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Tests;

/// <summary>The "Build your first endpoint" guide ticks each step at the right moment; every help link lands on a real page.</summary>
public class GuideAndHelpTests
{
    private static readonly string Root = RepoRoot();

    private static ConfigSnapshot Samples() => new(
        JsonSerializer.Deserialize<List<ApiService>>(File.ReadAllText(Path.Combine(Root, "samples/config/services.json")), AggregatorJson.Options)!,
        [], [], "test");

    private static int Done(RecipeForm form, bool testPassed = false) => FirstEndpointGuide.Progress(form, testPassed).Done;

    private static StepForm AddStep(RecipeForm form, string name, string endpoint)
    {
        var step = new StepForm { DisplayName = name, Alias = form.NewAlias(name), Endpoint = endpoint };
        form.Steps.Add(step);
        return step;
    }

    [Fact]
    public void The_guide_needs_the_sample_crm()
    {
        Assert.True(FirstEndpointGuide.Available(Samples()));
        Assert.False(FirstEndpointGuide.Available(new ConfigSnapshot([], [], [], "empty")));
    }

    [Fact]
    public void Each_step_of_the_guide_ticks_when_it_is_done_and_not_before()
    {
        var form = new RecipeForm();
        Assert.Equal(0, Done(form));

        form.Name = "Contacts with notes"; form.Route = "contacts/with-notes";
        Assert.Equal(1, Done(form));

        AddStep(form, "List contacts", "service-f-crm/listContacts");
        AddStep(form, "List orders", "service-f-crm/listOrders");
        var notes = AddStep(form, "Notes for one contact", "service-f-crm/getContactNotes");
        Assert.Equal(4, Done(form));

        notes.ForEach = "{{listOrders.body.orders[*].id}}"; // the wrong field: not done
        Assert.Equal(4, Done(form));
        notes.ForEach = "{{listOrders.body.orders[*].contactId}}";
        Assert.Equal(5, Done(form));

        notes.Bindings["path.contactId"] = new BindingForm { Mode = BindingMode.FromSource, Expression = "item", Suggested = true };
        Assert.Equal(6, Done(form));

        form.Response.Add(new ResponseField { Name = "contacts", Expression = "listContacts.body.contacts" }); // all of it: not yet
        form.Response.Add(new ResponseField { Name = "notes", Expression = "notesForOneContact[*].body.notes" });
        Assert.Equal(6, Done(form));
        form.Response[0].Expression = "listContacts.body.contacts | distinct('id')";
        Assert.Equal(7, Done(form));

        Assert.Equal((8, null), FirstEndpointGuide.Progress(form, testPassed: true));
    }

    [Fact]
    public void The_current_step_is_the_first_that_isnt_done()
    {
        var form = new RecipeForm { Name = "x", Route = "x" };
        AddStep(form, "List orders", "service-f-crm/listOrders"); // step 3 done before step 2

        Assert.Equal((2, (int?)1), FirstEndpointGuide.Progress(form, false));
    }

    [Fact]
    public void Every_help_link_in_the_app_lands_on_a_help_page_that_exists()
    {
        var used = Directory.GetFiles(Path.Combine(Root, "src/ApiAggregator.Admin/Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:HelpLink Topic|HelpTopic)=""([^""@]+)""").Select(m => (File: Path.GetFileName(f), Topic: m.Groups[1].Value)))
            .ToList();
        Assert.NotEmpty(used);

        var broken = used.Where(u => HelpTopics.Href(u.Topic) is null).Select(u => $"{u.File}: {u.Topic}").ToList();
        Assert.True(broken.Count == 0, "No help page for: " + string.Join(", ", broken));
    }

    [Theory]
    [InlineData("build", "/help/build")]
    [InlineData("history", "/help/publish#history")]
    [InlineData("sign-in", "/help/add-service#sign-in")]
    [InlineData("add-endpoint", "/help/add-service#by-hand")]
    [InlineData("loops#renaming", "/help/loops#renaming")]
    [InlineData("nothing-here", null)]
    public void Old_and_new_help_topics_resolve_to_their_page(string topic, string? expected) =>
        Assert.Equal(expected, HelpTopics.Href(topic));

    [Fact]
    public void Each_help_page_has_a_full_guide_that_exists()
    {
        var missing = HelpTopics.All
            .Select(t => t.Guide.Split('#')[0])
            .Where(g => !File.Exists(Path.Combine(Root, "docs/guides", g + ".md")))
            .ToList();
        Assert.Empty(missing);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root (the folder with CLAUDE.md).");
    }
}
