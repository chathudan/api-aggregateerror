using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiAggregator.Engine.Tests;

/// <summary>The file store's history rules (ADR 0012) and how a running gateway notices changes.</summary>
public sealed class FileConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aggregator-store-tests-" + Guid.NewGuid().ToString("n"));
    private static readonly ChangeInfo Change = new("qa-1", new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private FileConfigStore Store() =>
        new(Options.Create(new FileConfigOptions { Directory = _dir, ReloadCheckSeconds = 1 }), NullLogger<FileConfigStore>.Instance);

    private static Recipe Recipe(int version, string name = "Orders") => new() { Id = "orders", Name = name, Route = "orders", Version = version, Status = RecipeStatus.Published };

    [Fact]
    public async Task Saved_files_keep_apostrophes_as_typed_and_end_with_a_newline()
    {
        await Store().SaveDraftAsync(Recipe(1, "The caller's orders <latest>"));

        var text = await File.ReadAllTextAsync(Path.Combine(_dir, "drafts", "orders.json"));
        Assert.Contains("The caller's orders <latest>", text);
        Assert.DoesNotContain("\\u0027", text);
        Assert.EndsWith("\n", text);
        Assert.Equal("The caller's orders <latest>", (await Store().GetDraftAsync("orders"))!.Name); // reads back the same
    }

    [Fact]
    public async Task Publishing_the_same_version_twice_is_refused_and_the_first_entry_is_kept()
    {
        var store = Store();
        await store.PublishAsync(Recipe(1, "First"), Change);

        await Assert.ThrowsAsync<HistoryConflictException>(() => store.PublishAsync(Recipe(1, "Second"), Change));

        var entry = Assert.Single(await store.GetHistoryAsync("orders"));
        Assert.Equal("First", entry.Recipe!.Name);
        Assert.Equal("First", (await store.GetSnapshotAsync()).GetRecipe("orders")!.Name); // nothing went live
    }

    [Fact]
    public async Task A_history_entry_records_who_when_and_what_was_restored()
    {
        var store = Store();
        await store.PublishAsync(Recipe(1), Change);
        await store.PublishAsync(Recipe(2), Change with { RestoredFrom = 1 });

        var history = await store.GetHistoryAsync("orders");

        Assert.Equal([2, 1], history.Select(e => e.Version));
        Assert.Equal(HistoryAction.RolledBack, history[0].Action);
        Assert.Equal(1, history[0].RestoredFrom);
        Assert.Equal(HistoryAction.Published, history[1].Action);
        Assert.All(history, e => { Assert.Equal("qa-1", e.By); Assert.Equal(Change.At, e.At); });
    }

    [Fact]
    public async Task A_history_file_from_before_adr_0012_is_read_without_who_and_when()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "history"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "history", "orders.v3.json"), """{ "id": "orders", "name": "Old", "version": 3, "route": "orders" }""");

        var entry = Assert.Single(await Store().GetHistoryAsync("orders"));

        Assert.Equal(3, entry.Version);
        Assert.Equal(HistoryAction.Published, entry.Action);
        Assert.Null(entry.By);
        Assert.Null(entry.At);
        Assert.Equal("Old", entry.Recipe!.Name);
    }

    [Fact]
    public async Task Unpublishing_keeps_the_history_and_adds_an_unpublished_entry()
    {
        var store = Store();
        await store.PublishAsync(Recipe(1), Change);

        Assert.True(await store.UnpublishAsync("orders", Change));
        Assert.False(await store.UnpublishAsync("orders", Change)); // nothing live any more

        var history = await store.GetHistoryAsync("orders");
        Assert.Equal([HistoryAction.Unpublished, HistoryAction.Published], history.Select(e => e.Action));
        Assert.Null(history[0].Recipe);
        Assert.Equal(["orders"], await store.GetHistoryIdsAsync());
    }

    [Fact]
    public async Task Unpublishing_a_recipe_added_by_hand_keeps_its_content_so_it_can_be_restored()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "recipes"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "recipes", "orders.json"), """{ "id": "orders", "name": "By hand", "version": 1, "status": "published", "route": "orders" }""");
        var store = Store();

        Assert.True(await store.UnpublishAsync("orders", Change));

        var history = await store.GetHistoryAsync("orders");
        Assert.Equal([HistoryAction.Unpublished, HistoryAction.Published], history.Select(e => e.Action));
        var kept = history[1];
        Assert.Equal("By hand", kept.Recipe!.Name);
        Assert.Null(kept.By); // who added it by hand isn't known
        Assert.Null(kept.At);
    }

    [Fact]
    public async Task A_running_gateway_stops_serving_a_recipe_another_process_unpublished()
    {
        await Store().PublishAsync(Recipe(1), Change);
        var gateway = Store();
        Assert.NotNull((await gateway.GetSnapshotAsync()).GetRecipe("orders"));

        await Store().UnpublishAsync("orders", Change); // the admin API, in another process
        await Task.Delay(TimeSpan.FromSeconds(1.2));     // ReloadCheckSeconds = 1

        Assert.Null((await gateway.GetSnapshotAsync()).GetRecipe("orders"));
    }

    [Fact]
    public async Task History_of_one_recipe_never_includes_another_whose_id_starts_the_same()
    {
        var store = Store();
        await store.PublishAsync(Recipe(1), Change);
        await store.PublishAsync(new Recipe { Id = "orders-archive", Route = "orders/archive", Version = 1, Status = RecipeStatus.Published }, Change);

        Assert.Single(await store.GetHistoryAsync("orders"));
        Assert.Equal(["orders", "orders-archive"], await store.GetHistoryIdsAsync());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("orders*")]
    [InlineData("../orders")]
    [InlineData("Orders")]
    public async Task History_ids_with_wildcards_or_paths_are_refused(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store().GetHistoryAsync(id));
    }

    [Fact]
    public async Task Deleting_a_draft_or_service_that_does_not_exist_returns_false()
    {
        var store = Store();

        Assert.False(await store.DeleteDraftAsync("orders"));
        Assert.False(await store.DeleteServiceAsync("nothing"));
    }
}
