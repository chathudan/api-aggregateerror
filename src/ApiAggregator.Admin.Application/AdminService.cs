using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;
using Microsoft.Extensions.Logging;

namespace ApiAggregator.Admin.Application;

/// <summary>
/// Every admin use case, with its business rules and role checks (ADR 0014). The admin pages call it directly and
/// /admin/* wraps it, so both follow exactly the same rules. Every method takes the <see cref="AdminActor"/> first
/// and refuses when the role is missing, whatever the caller has hidden or shown.
/// Messages are plain English and never contain secret values.
/// </summary>
public sealed class AdminService(
    IConfigStore store,
    DownstreamAuthenticator auth,
    CompositionExecutor executor,
    SourceCatalog sources,
    OpenApiImporter importer,
    IEnumerable<IAuthStep> authSteps,
    ISecretResolver secrets,
    ISecretWriter secretWriter,
    AdminOptions options,
    ILogger<AdminService> log)
{
    private readonly ConcurrentDictionary<string, string> _passedTests = new(); // recipe id -> hash of the draft that last passed Try it
    private readonly ConcurrentDictionary<string, SignInStatus> _signInStatus = new(StringComparer.Ordinal); // auth profile id -> last check

    // Publish, roll back, delete endpoint and delete service run one at a time, so "no published endpoint uses this
    // service" can't change between the check and the delete. (The file store is single-instance; see ADR 0007.)
    private readonly SemaphoreSlim _liveChanges = new(1, 1);

    // ---------------------------------------------------------------- roles

    private static AdminFailure? NeedsAuthor(AdminActor actor) =>
        actor.IsAuthor ? null : Refuse.Forbidden("You need the author or publisher role to do this.");

    private static AdminFailure? NeedsPublisher(AdminActor actor) =>
        actor.IsPublisher ? null : Refuse.Forbidden("You need the publisher role to do this. Ask a publisher.");

    private static AdminFailure? NeedsAnyRole(AdminActor actor) =>
        actor.HasAnyRole ? null : Refuse.Forbidden("You need the author, publisher or admin role to see this.");

    private static AdminFailure? NeedsAdmin(AdminActor actor) =>
        actor.IsAdmin ? null : Refuse.Forbidden("You need the admin role to change browser access. Ask an administrator.");

    // ---------------------------------------------------------------- services and endpoints

    public async Task<AdminResult<IReadOnlyList<ApiService>>> GetServicesAsync(AdminActor actor, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        return (await store.GetSnapshotAsync(ct)).Services.OrderBy(s => s.Id).ToList();
    }

    /// <summary>Adds or replaces a service. Renaming is saving it with a new name: the id stays, so endpoints keep working.</summary>
    public async Task<AdminResult<ApiService>> SaveServiceAsync(AdminActor actor, string id, ApiService service, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        if (service.Id != id) return Refuse.BadRequest("The id in the URL and the body must match.");
        if (!Uri.TryCreate(service.BaseUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return Refuse.BadRequest("Base URL must be an http or https address.");
        if (string.IsNullOrWhiteSpace(service.Name) || service.Name.Length > 100 || service.Name.Any(char.IsControl))
            return Refuse.BadRequest("Give the service a name of up to 100 characters.");

        // A sign-in that doesn't exist would leave the service called without credentials: refuse it here.
        var config = await store.GetSnapshotAsync(ct);
        foreach (var signIn in new[] { service.AuthProfileId }.Concat(service.Endpoints.Select(e => e.AuthProfileId)).OfType<string>().Distinct())
            if (config.GetAuthProfile(signIn) is null)
                return Refuse.BadRequest($"The sign-in '{signIn}' doesn't exist. Save the sign-in first, or pick another one.");

        await store.SaveServiceAsync(service, ct);
        return service;
    }

    /// <summary>Which aggregated endpoints call this service. Shown before deleting it.</summary>
    public async Task<AdminResult<ServiceUsage>> GetServiceUsageAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var (published, drafts) = await UsageOf(id, ct);
        return new ServiceUsage(id, published, drafts);
    }

    /// <summary>Blocked while a published endpoint uses the service. Its sign-in is kept: other services may share it.</summary>
    public async Task<AdminResult<ServiceDeleted>> DeleteServiceAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        if ((await store.GetSnapshotAsync(ct)).GetService(id) is null) return Refuse.NotFound($"Service '{id}' not found.");

        await _liveChanges.WaitAsync(ct);
        try
        {
            var (published, drafts) = await UsageOf(id, ct);
            if (published.Count > 0)
                return Refuse.Conflict(
                    $"Published endpoints still use this service: {string.Join(", ", published.Select(r => r.Name))}. Change or delete those endpoints first.",
                    ("published", published));

            await store.DeleteServiceAsync(id, ct);
            log.LogInformation("Service {ServiceId} deleted by {Subject}", id, actor.Subject);
            return new ServiceDeleted(id, drafts);
        }
        finally { _liveChanges.Release(); }
    }

    public async Task<AdminResult<ImportPreview>> PreviewOpenApiAsync(AdminActor actor, OpenApiImportRequest request, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        try { return await importer.PreviewAsync(request, ct); }
        catch (OpenApiImportException ex) { return Refuse.BadRequest(ex.Message); }
    }

    public async Task<AdminResult<ApiService>> ImportOpenApiAsync(AdminActor actor, string id, OpenApiImportRequest request, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var service = (await store.GetSnapshotAsync(ct)).GetService(id);
        if (service is null) return Refuse.NotFound($"Service '{id}' not found. Create it first.");
        try
        {
            var updated = await importer.ImportAsync(service, request, ct);
            await store.SaveServiceAsync(updated, ct);
            return updated;
        }
        catch (OpenApiImportException ex) { return Refuse.BadRequest(ex.Message); }
    }

    /// <summary>For endpoints added by hand: a sample response becomes a schema (drives the wizard's dropdowns).</summary>
    public AdminResult<JsonNode> InferSchema(AdminActor actor, JsonNode? sample) =>
        NeedsAuthor(actor) is { } no ? no : SourceCatalog.InferSchema(sample);

    // ---------------------------------------------------------------- what this installation allows

    public AdminResult<Capabilities> GetCapabilities(AdminActor actor)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        return new Capabilities(
            // The sample configuration (samples/config) carries SAMPLE-DATA.md; the UI shows a "Sample data" badge for it.
            SampleData: File.Exists(Path.Combine(Path.GetFullPath(options.ConfigDirectory), "SAMPLE-DATA.md")),
            // So the UI can hide publish, roll back and delete for authors. The service still checks the role on every call.
            CanPublish: actor.IsPublisher,
            options.RequireTestBeforePublish,
            // Development: secret values may be stored. Elsewhere the UI shows the secret NAME to put in Key Vault.
            options.CanSaveSecrets,
            AuthKinds());
    }

    public AdminResult<IReadOnlyList<AuthKindInfo>> GetAuthKinds(AdminActor actor) =>
        NeedsAuthor(actor) is { } no ? no : AdminResult<IReadOnlyList<AuthKindInfo>>.Success(AuthKinds());

    private List<AuthKindInfo> AuthKinds() => authSteps.Select(s => new AuthKindInfo(s.Kind, s.RequiredSettings)).ToList();

    // ---------------------------------------------------------------- secrets (values never come back)

    public async Task<AdminResult<SecretSaved>> SaveSecretAsync(AdminActor actor, string name, string? value, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        if (!options.CanSaveSecrets)
            return Refuse.BadRequest("Secrets are managed in Key Vault in this environment. Ask an admin to add it there.");
        if (!IsSecretName(name)) return Refuse.BadRequest("Secret names use lowercase letters, digits and dashes.");
        if (string.IsNullOrEmpty(value) || value.Length > 4096) return Refuse.BadRequest("Enter a value (up to 4096 characters).");

        await secretWriter.SaveAsync(name, value, ct);
        return new SecretSaved(name, $"secret:{name}");
    }

    public async Task<AdminResult<SecretExists>> SecretExistsAsync(AdminActor actor, string name, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        if (!IsSecretName(name)) return Refuse.BadRequest(null);
        try { await secrets.GetAsync(name, ct); return new SecretExists(name, true); }
        catch (AuthFailedException) { return new SecretExists(name, false); }
    }

    // ---------------------------------------------------------------- auth profiles

    public async Task<AdminResult<IReadOnlyList<AuthProfile>>> GetAuthProfilesAsync(AdminActor actor, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        return (await store.GetSnapshotAsync(ct)).AuthProfiles.OrderBy(p => p.Id).ToList();
    }

    public async Task<AdminResult<AuthProfile>> SaveAuthProfileAsync(AdminActor actor, string id, AuthProfile profile, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        if (profile.Id != id) return Refuse.BadRequest("The id in the URL and the body must match.");
        var problems = auth.Check(profile).ToList();
        if (problems.Count > 0) return Refuse.BadRequest("The auth profile has problems.", ("errors", problems));
        await store.SaveAuthProfileAsync(profile, ct);
        return profile;
    }

    /// <summary>
    /// Signs in once with the profile (login or token call), so problems show up before anyone builds an endpoint.
    /// The result is remembered (in memory, until restart) for the dashboard and the Services page.
    /// </summary>
    public async Task<AdminResult<SignInCheck>> CheckSignInAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var config = await store.GetSnapshotAsync(ct);
        var profile = config.GetAuthProfile(id);
        if (profile is null) return Refuse.NotFound($"Sign-in '{id}' not found.");
        var status = await CheckSignIn(profile, config, ct);
        _signInStatus[id] = status;
        return new SignInCheck(status.State != SignInState.Failed, status.Message);
    }

    public async Task<AdminResult<IReadOnlyList<SignInStatus>>> CheckAllSignInsAsync(AdminActor actor, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var config = await store.GetSnapshotAsync(ct);
        foreach (var profile in config.AuthProfiles)
            _signInStatus[profile.Id] = await CheckSignIn(profile, config, ct);
        return SignInStatuses(config);
    }

    /// <summary>
    /// Last check per sign-in: signedIn (logged in or got a token), settingsOk (complete; the service checks it on the
    /// first real call), failed, or notChecked (since this app started).
    /// </summary>
    public async Task<AdminResult<IReadOnlyList<SignInStatus>>> GetSignInStatusAsync(AdminActor actor, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        return SignInStatuses(await store.GetSnapshotAsync(ct));
    }

    // ---------------------------------------------------------------- aggregated endpoints (recipes)

    public async Task<AdminResult<IReadOnlyList<RecipeSummary>>> GetRecipesAsync(AdminActor actor, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var published = (await store.GetSnapshotAsync(ct)).Recipes;
        var drafts = await store.GetDraftsAsync(ct);
        var current = published.Select(r => r.Id).Union(drafts.Select(d => d.Id)).ToHashSet();

        var list = current.Select(id =>
        {
            var p = published.FirstOrDefault(r => r.Id == id);
            var d = drafts.FirstOrDefault(r => r.Id == id);
            var r = d ?? p!;
            return new RecipeSummary(id, r.Name, r.Method, "/bff/" + r.Route, p?.Version, d is not null, Deleted: false);
        }).ToList();

        // Deleted endpoints: not live and no draft, but their history is kept so they can be restored.
        foreach (var id in (await store.GetHistoryIdsAsync(ct)).Where(id => !current.Contains(id)))
        {
            var last = (await store.GetHistoryAsync(id, ct)).FirstOrDefault(e => e.Recipe is not null)?.Recipe;
            if (last is not null) list.Add(new RecipeSummary(id, last.Name, last.Method, "/bff/" + last.Route, null, false, Deleted: true));
        }
        return list.OrderBy(r => r.Id).ToList();
    }

    /// <summary>The draft if there is one, else the live version.</summary>
    public async Task<AdminResult<Recipe>> GetRecipeAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var recipe = await store.GetDraftAsync(id, ct) ?? (await store.GetSnapshotAsync(ct)).GetRecipe(id);
        return recipe is null ? Refuse.NotFound() : recipe;
    }

    public async Task<AdminResult<Recipe>> SaveDraftAsync(AdminActor actor, string id, Recipe draft, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        if (draft.Id != id) return Refuse.BadRequest("The id in the URL and the body must match.");
        var saved = Clone(draft, status: RecipeStatus.Draft, version: await NextVersion(id, ct));
        await store.SaveDraftAsync(saved, ct);
        return saved;
    }

    /// <summary>Discard changes: removes the draft only. The live version (if any) keeps serving.</summary>
    public async Task<AdminResult<DraftDiscarded>> DiscardDraftAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        if (!await store.DeleteDraftAsync(id, ct)) return Refuse.NotFound("There are no unpublished changes to discard.");
        _passedTests.TryRemove(id, out _);
        log.LogInformation("Draft {RecipeId} discarded by {Subject}", id, actor.Subject);
        var live = (await store.GetSnapshotAsync(ct)).GetRecipe(id);
        return new DraftDiscarded(id, live?.Version);
    }

    public async Task<AdminResult<ValidationResult>> ValidateAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var draft = await store.GetDraftAsync(id, ct);
        if (draft is null) return Refuse.NotFound("Save the draft first.");
        return RecipeValidator.Validate(draft, await store.GetSnapshotAsync(ct));
    }

    /// <summary>Options for the "use value from" dropdown.</summary>
    public async Task<AdminResult<IReadOnlyList<SourceOption>>> GetSourcesAsync(AdminActor actor, string id, string? beforeStep, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var draft = await store.GetDraftAsync(id, ct) ?? (await store.GetSnapshotAsync(ct)).GetRecipe(id);
        if (draft is null) return Refuse.NotFound();
        return AdminResult<IReadOnlyList<SourceOption>>.Success(sources.For(draft, beforeStep, await store.GetSnapshotAsync(ct)));
    }

    /// <summary>
    /// Try it: runs the saved draft for real (no cache, with a trace). <paramref name="caller"/> is the person as the
    /// engine sees them (the host builds it: subject, claims, their token for "use the caller's own token" services).
    /// A pass is remembered by the draft's content hash, which publishing checks.
    /// </summary>
    public async Task<AdminResult<TestOutcome>> TestAsync(AdminActor actor, string id, TestRequest test, CallerContext caller, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var draft = await store.GetDraftAsync(id, ct);
        if (draft is null) return Refuse.NotFound("Save the draft first.");

        var validation = RecipeValidator.Validate(draft, await store.GetSnapshotAsync(ct));
        if (!validation.IsValid) return new TestOutcome(false, null, null, null, null, null, null, validation.Issues, validation.Warnings);

        var result = await executor.ExecuteAsync(draft, test.ToInbound(), caller, ExecutionMode.AdminTest, ct);
        var passed = result.StatusCode == 200 && !result.Partial;
        if (passed) _passedTests[id] = Hash(draft);

        return new TestOutcome(passed, result.StatusCode, result.DurationMs, result.Partial, result.Trace, result.Body, caller.CorrelationId, null, validation.Warnings);
    }

    public async Task<AdminResult<PublishResult>> PublishAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsPublisher(actor) is { } no) return no;
        var draft = await store.GetDraftAsync(id, ct);
        if (draft is null) return Refuse.NotFound("There's no draft to publish.");

        await _liveChanges.WaitAsync(ct);
        try
        {
            var validation = RecipeValidator.Validate(draft, await store.GetSnapshotAsync(ct));
            if (!validation.IsValid) return Refuse.BadRequest("Fix the problems before publishing.", ("issues", validation.Issues));
            if (options.RequireTestBeforePublish && (!_passedTests.TryGetValue(id, out var hash) || hash != Hash(draft)))
                return Refuse.BadRequest("Run a successful test of this exact draft before publishing.");

            // The version comes from the history, not the draft: a roll back or a delete may have happened since it was saved.
            var live = Clone(draft, status: RecipeStatus.Published, version: await NextVersion(id, ct));
            try { await store.PublishAsync(live, Change(actor), ct); }
            catch (HistoryConflictException) { return PublishedMeanwhile(); }

            await store.DeleteDraftAsync(id, ct); // it is the live version now, so there are no unpublished changes left
            _passedTests.TryRemove(id, out _);
            log.LogInformation("Recipe {RecipeId} v{Version} published by {Subject}", id, live.Version, actor.Subject);
            // Warnings never block publishing; they are returned so the caller can show them.
            return new PublishResult(id, live.Version, "/bff/" + live.Route, validation.Warnings);
        }
        finally { _liveChanges.Release(); }
    }

    public async Task<AdminResult<IReadOnlyList<HistoryRow>>> GetHistoryAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var liveVersion = (await store.GetSnapshotAsync(ct)).GetRecipe(id)?.Version;
        return (await store.GetHistoryAsync(id, ct)).Select(e => new HistoryRow(
            e.Version, e.Action, e.At, e.By, e.ByName, e.RestoredFrom, e.Recipe?.Name, e.Recipe?.Route,
            IsLive: e.Action != HistoryAction.Unpublished && e.Version == liveVersion)).ToList();
    }

    /// <summary>One version's content, for "View" in the History panel (read only).</summary>
    public async Task<AdminResult<Recipe>> GetVersionAsync(AdminActor actor, string id, int version, CancellationToken ct = default)
    {
        if (NeedsAuthor(actor) is { } no) return no;
        var recipe = (await store.GetHistoryAsync(id, ct)).FirstOrDefault(e => e.Version == version && e.Recipe is not null)?.Recipe;
        return recipe is null ? Refuse.NotFound($"Version {version} isn't in the history.") : recipe;
    }

    /// <summary>Publishes an old version's content as a NEW version, so history stays append-only.</summary>
    public async Task<AdminResult<RollbackResult>> RollBackAsync(AdminActor actor, string id, int version, CancellationToken ct = default)
    {
        if (NeedsPublisher(actor) is { } no) return no;
        var target = (await store.GetHistoryAsync(id, ct)).FirstOrDefault(e => e.Version == version && e.Recipe is not null)?.Recipe;
        if (target is null) return Refuse.NotFound($"Version {version} isn't in the history.");

        await _liveChanges.WaitAsync(ct);
        try
        {
            // Services may have changed since that version was live: never put a broken endpoint live.
            var validation = RecipeValidator.Validate(target, await store.GetSnapshotAsync(ct));
            if (!validation.IsValid)
                return Refuse.BadRequest(
                    $"Version {version} can't go live as it is, because the services it uses have changed. Open it, fix the problems, then test and publish.",
                    ("issues", validation.Issues));

            var live = Clone(target, status: RecipeStatus.Published, version: await NextVersion(id, ct));
            try { await store.PublishAsync(live, Change(actor, restoredFrom: version), ct); }
            catch (HistoryConflictException) { return PublishedMeanwhile(); }

            log.LogInformation("Recipe {RecipeId} rolled back to v{From} as v{Version} by {Subject}", id, version, live.Version, actor.Subject);
            return new RollbackResult(id, live.Version, version);
        }
        finally { _liveChanges.Release(); }
    }

    /// <summary>Delete an endpoint: the gateway stops serving it and its draft is discarded. Its history is kept, so it can be restored.</summary>
    public async Task<AdminResult<EndpointDeleted>> DeleteEndpointAsync(AdminActor actor, string id, CancellationToken ct = default)
    {
        if (NeedsPublisher(actor) is { } no) return no;
        await _liveChanges.WaitAsync(ct);
        try
        {
            var liveVersion = (await store.GetSnapshotAsync(ct)).GetRecipe(id)?.Version;
            var unpublished = await store.UnpublishAsync(id, Change(actor), ct);
            var draftDiscarded = await store.DeleteDraftAsync(id, ct);
            if (!unpublished && !draftDiscarded) return Refuse.NotFound($"There's no endpoint '{id}' to delete.");

            _passedTests.TryRemove(id, out _);
            log.LogInformation("Recipe {RecipeId} deleted (unpublished v{Version}, draft discarded: {DraftDiscarded}) by {Subject}", id, liveVersion, draftDiscarded, actor.Subject);
            return new EndpointDeleted(id, unpublished ? liveVersion : null, draftDiscarded);
        }
        finally { _liveChanges.Release(); }
    }

    // ---------------------------------------------------------------- browser access (CORS, ADR 0015)

    /// <summary>What the gateway allows browsers to do, built exactly as the gateway builds it. Anyone with a role may look.</summary>
    public async Task<AdminResult<BrowserAccessView>> GetBrowserAccessAsync(AdminActor actor, CancellationToken ct = default)
    {
        if (NeedsAnyRole(actor) is { } no) return no;
        var policy = await BrowserPolicy(ct);
        return new BrowserAccessView(policy.Origins, policy.Skipped, policy.Patterns, policy.Methods, policy.Headers,
            BrowserAccessPolicy.ExposedHeaders, policy.PreflightMaxAgeSeconds, AllowsCredentials: false, OriginRules.MaxAdminEntries, actor.IsAdmin);
    }

    /// <summary>"Test an origin": would a page on this origin be allowed, and why.</summary>
    public async Task<AdminResult<OriginTest>> TestOriginAsync(AdminActor actor, OriginTestRequest request, CancellationToken ct = default)
    {
        if (NeedsAnyRole(actor) is { } no) return no;
        return (await BrowserPolicy(ct)).Test(request.Origin, options.IsDevelopment);
    }

    /// <summary>Adds an origin within the operations limits. Recorded in the history; the gateway applies it without a restart.</summary>
    public async Task<AdminResult<BrowserOrigin>> AddBrowserOriginAsync(AdminActor actor, AddOriginRequest request, CancellationToken ct = default)
    {
        if (NeedsAdmin(actor) is { } no) return no;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

        await _liveChanges.WaitAsync(ct);
        try
        {
            var current = (await store.GetSnapshotAsync(ct)).BrowserOrigins;
            var check = OriginRules.CheckAdminEntry(request.Origin, note, current, options.BrowserAccess, options.IsDevelopment);
            if (!check.Ok) return Refuse.BadRequest(check.Problem, ("field", check.Problem == OriginRules.CheckNote(note) ? "note" : "origin"));

            var now = DateTimeOffset.UtcNow;
            var added = new BrowserOrigin { Origin = check.Value!, Note = note, AddedAt = now, AddedBy = actor.Subject, AddedByName = actor.DisplayName };
            var change = new BrowserAccessHistoryEntry
            {
                Number = await NextBrowserAccessNumber(ct), Action = BrowserAccessAction.Added, Origin = added.Origin, Note = note,
                At = now, By = actor.Subject, ByName = actor.DisplayName
            };
            try { await store.SaveBrowserOriginsAsync([.. current, added], change, ct); }
            catch (HistoryConflictException) { return ChangedMeanwhile(); }

            log.LogInformation("Browser access change {Number} ({Action}) by {Subject}", change.Number, change.Action, actor.Subject);
            return added;
        }
        finally { _liveChanges.Release(); }
    }

    /// <summary>
    /// Edits an origin added here: its address, its note, or both, with the same checks as adding one. Recorded in the
    /// history with what it was before. Origins operations set can only be changed in the app settings.
    /// </summary>
    public async Task<AdminResult<BrowserOrigin>> ChangeBrowserOriginAsync(AdminActor actor, ChangeOriginRequest request, CancellationToken ct = default)
    {
        if (NeedsAdmin(actor) is { } no) return no;
        var value = request.Origin?.Trim() ?? "";
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();

        await _liveChanges.WaitAsync(ct);
        try
        {
            var current = (await store.GetSnapshotAsync(ct)).BrowserOrigins;
            var entry = current.FirstOrDefault(e => string.Equals(e.Origin, value, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return NotOnTheList(value);

            // Checked like a new entry, against the others: the same rules can't be got round by editing.
            var others = current.Where(e => e != entry).ToList();
            var check = OriginRules.CheckAdminEntry(request.NewOrigin, note, others, options.BrowserAccess, options.IsDevelopment);
            if (!check.Ok) return Refuse.BadRequest(check.Problem, ("field", check.Problem == OriginRules.CheckNote(note) ? "note" : "origin"));
            if (check.Value == entry.Origin && note == entry.Note) return entry; // nothing changed: nothing to record

            var changed = new BrowserOrigin
            {
                Origin = check.Value!, Note = note, AddedAt = entry.AddedAt, AddedBy = entry.AddedBy, AddedByName = entry.AddedByName
            };
            var change = new BrowserAccessHistoryEntry
            {
                Number = await NextBrowserAccessNumber(ct), Action = BrowserAccessAction.Changed, Origin = changed.Origin, Note = note,
                PreviousOrigin = entry.Origin, PreviousNote = entry.Note,
                At = DateTimeOffset.UtcNow, By = actor.Subject, ByName = actor.DisplayName
            };
            // Same position in the list, so the page doesn't reorder.
            try { await store.SaveBrowserOriginsAsync(current.Select(e => e == entry ? changed : e).ToList(), change, ct); }
            catch (HistoryConflictException) { return ChangedMeanwhile(); }

            log.LogInformation("Browser access change {Number} ({Action}) by {Subject}", change.Number, change.Action, actor.Subject);
            return changed;
        }
        finally { _liveChanges.Release(); }
    }

    /// <summary>Removes an origin added here. Origins operations set can only be changed in the app settings.</summary>
    public async Task<AdminResult<OriginRemoved>> RemoveBrowserOriginAsync(AdminActor actor, string? origin, CancellationToken ct = default)
    {
        if (NeedsAdmin(actor) is { } no) return no;
        var value = origin?.Trim() ?? "";

        await _liveChanges.WaitAsync(ct);
        try
        {
            var current = (await store.GetSnapshotAsync(ct)).BrowserOrigins;
            // Match the entry as stored, so an entry the gateway skips (not in the exact form) can still be removed.
            var entry = current.FirstOrDefault(e => string.Equals(e.Origin, value, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return NotOnTheList(value);

            var change = new BrowserAccessHistoryEntry
            {
                Number = await NextBrowserAccessNumber(ct), Action = BrowserAccessAction.Removed, Origin = entry.Origin, Note = entry.Note,
                At = DateTimeOffset.UtcNow, By = actor.Subject, ByName = actor.DisplayName
            };
            try { await store.SaveBrowserOriginsAsync(current.Where(e => e != entry).ToList(), change, ct); }
            catch (HistoryConflictException) { return ChangedMeanwhile(); }

            log.LogInformation("Browser access change {Number} ({Action}) by {Subject}", change.Number, change.Action, actor.Subject);
            return new OriginRemoved(entry.Origin);
        }
        finally { _liveChanges.Release(); }
    }

    /// <summary>Who added or removed which origin, and when. Newest first.</summary>
    public async Task<AdminResult<IReadOnlyList<BrowserAccessHistoryEntry>>> GetBrowserAccessHistoryAsync(AdminActor actor, CancellationToken ct = default)
    {
        if (NeedsAnyRole(actor) is { } no) return no;
        return AdminResult<IReadOnlyList<BrowserAccessHistoryEntry>>.Success(await store.GetBrowserAccessHistoryAsync(ct));
    }

    private async Task<BrowserAccessPolicy> BrowserPolicy(CancellationToken ct) =>
        BrowserAccessPolicy.From(await store.GetSnapshotAsync(ct), options.BrowserAccess, options.IsDevelopment);

    /// <summary>History numbers are never reused, like recipe versions.</summary>
    private async Task<int> NextBrowserAccessNumber(CancellationToken ct) =>
        (await store.GetBrowserAccessHistoryAsync(ct)).Select(e => e.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>An origin to change or remove that isn't among those added here: set by operations, or not there at all.</summary>
    private AdminFailure NotOnTheList(string value)
    {
        var canonical = OriginRules.CheckOrigin(value, isDevelopment: true).Value;
        return canonical is not null && OriginRules.OperationsOrigins(options.BrowserAccess, options.IsDevelopment).Contains(canonical)
            ? Refuse.BadRequest("Operations set this origin in the app settings, so it can't be changed here. Ask operations.")
            : Refuse.NotFound("That origin isn't on the list.");
    }

    private static AdminFailure ChangedMeanwhile() =>
        Refuse.Conflict("Someone else changed browser access at the same moment. Reload the page and try again.");

    // ---------------------------------------------------------------- helpers

    private static Recipe Clone(Recipe r, RecipeStatus status, int version) =>
        JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(r, AggregatorJson.Options), AggregatorJson.Options) is { } copy
            ? new Recipe
            {
                Id = copy.Id, Name = copy.Name, Version = version, Status = status, Method = copy.Method, Route = copy.Route,
                RequireAuthentication = copy.RequireAuthentication, RequiredScope = copy.RequiredScope, Inputs = copy.Inputs,
                Steps = copy.Steps, Combine = copy.Combine, Response = copy.Response, Cache = copy.Cache, TimeoutMs = copy.TimeoutMs
            }
            : throw new InvalidOperationException("Could not copy the recipe.");

    /// <summary>What a test pass is remembered by. Status and version don't change what the recipe does.</summary>
    private static string Hash(Recipe r)
    {
        var content = Clone(r, RecipeStatus.Draft, 0);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, AggregatorJson.Options))));
    }

    /// <summary>
    /// Signs in once (or, for kinds that only the service can check, checks the settings and secrets).
    /// Messages are the plain-English ones from the auth steps: they never contain secret values.
    /// </summary>
    private async Task<SignInStatus> CheckSignIn(AuthProfile profile, ConfigSnapshot config, CancellationToken ct)
    {
        // The address to try: a service that uses this sign-in, itself or for one of its endpoints.
        var service = config.Services.FirstOrDefault(s => s.AuthProfileId == profile.Id || s.Endpoints.Any(e => e.AuthProfileId == profile.Id));
        using var probe = new HttpRequestMessage(HttpMethod.Get, service?.BaseUrl ?? "http://localhost/");
        var signsInOnline = profile.Steps.Any(s => s.Kind is "SessionLogin" or "OAuthClientCredentials");
        try
        {
            auth.Invalidate(profile, "sign-in check requested");
            await auth.ApplyAsync(profile, probe, CallerContext.Anonymous(), ct);
            return signsInOnline
                ? new SignInStatus(profile.Id, SignInState.SignedIn, "Signed in.", DateTimeOffset.UtcNow)
                : new SignInStatus(profile.Id, SignInState.SettingsOk, "Settings and secrets are complete. This sign-in type is checked by the service on the first real call.", DateTimeOffset.UtcNow);
        }
        catch (AuthFailedException ex)
        {
            // Passing the caller's token on can't be checked without a caller: that's expected, not a failure.
            if (profile.Steps.All(s => s.Kind == "PassThroughUserToken"))
                return new SignInStatus(profile.Id, SignInState.SettingsOk, "Passes on the caller's own token. Check it with Try it, signed in as a test user.", DateTimeOffset.UtcNow);
            return new SignInStatus(profile.Id, SignInState.Failed, ex.Message, DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException) { return new SignInStatus(profile.Id, SignInState.Failed, "Couldn't reach the sign-in address. Check the URL and that the service is running.", DateTimeOffset.UtcNow); }
    }

    private List<SignInStatus> SignInStatuses(ConfigSnapshot config) =>
        config.AuthProfiles.OrderBy(p => p.Id)
            .Select(p => _signInStatus.TryGetValue(p.Id, out var last) ? last : new SignInStatus(p.Id, SignInState.NotChecked, "Not checked yet.", null))
            .ToList();

    /// <summary>Who and when, for the history (ADR 0012): the person's id and, when their sign-in has one, their name.</summary>
    private static ChangeInfo Change(AdminActor actor, int? restoredFrom = null) => new(actor.Subject, DateTimeOffset.UtcNow, restoredFrom, actor.DisplayName);

    /// <summary>One more than any version ever used, live or in the history: version numbers are never reused (ADR 0012).</summary>
    private async Task<int> NextVersion(string id, CancellationToken ct)
    {
        var live = (await store.GetSnapshotAsync(ct)).GetRecipe(id)?.Version ?? 0;
        var history = (await store.GetHistoryAsync(id, ct)).Select(e => e.Version).DefaultIfEmpty(0).Max();
        return Math.Max(live, history) + 1;
    }

    private static AdminFailure PublishedMeanwhile() =>
        Refuse.Conflict("Someone else published this endpoint at the same moment. Reload the page, check the history, and try again.");

    /// <summary>Published and draft endpoints with a step that calls the service.</summary>
    private async Task<(List<RecipeRef> Published, List<RecipeRef> Drafts)> UsageOf(string serviceId, CancellationToken ct)
    {
        bool Uses(Recipe r) => r.Steps.Any(s => s.Endpoint.StartsWith(serviceId + "/", StringComparison.OrdinalIgnoreCase));
        RecipeRef Ref(Recipe r) => new(r.Id, string.IsNullOrEmpty(r.Name) ? r.Id : r.Name);
        var published = (await store.GetSnapshotAsync(ct)).Recipes.Where(Uses).Select(Ref).OrderBy(r => r.Id).ToList();
        var drafts = (await store.GetDraftsAsync(ct)).Where(Uses).Select(Ref).OrderBy(r => r.Id).ToList();
        return (published, drafts);
    }

    private static bool IsSecretName(string name) =>
        name.Length is > 0 and <= 80 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
