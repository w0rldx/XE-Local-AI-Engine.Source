namespace XE_Local_AI_Engine.Tests.CustomTools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The batched resolution seam: one node kill-switch check and one custom-tool store read per resolution operation,
///     however many names the offer carries — and NO cache, so a library edit between two resolutions is seen by the
///     second one. Offer semantics (enabled AND acknowledged AND a valid name, a compilable schema, the unconditional
///     approval wrap) are unchanged by the batching and are pinned here alongside the read count.
/// </summary>
public sealed class CustomToolCatalogTests
{
    [Test]
    public async Task TryResolveManyAsync_ForSeveralNames_ReadsTheStoreOnce()
    {
        // The whole point of O1: k requested names cost ONE scope and ONE ListAsync, not k of each.
        var names = new[]
        {
            "custom__alpha",
            "custom__bravo",
            "custom__charlie",
            "custom__delta",
            "custom__echo"
        };
        var store = BuildStore([.. names.Select(name => Record(name))]);
        var storeResolutions = new ReadCounter();
        var catalog = CreateCatalog(store, storeResolutions, Settings(customToolsEnabled: true));

        var resolved = await catalog.TryResolveManyAsync(names, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 5, resolved.Count, "every offerable requested name must resolve");
        AssertEx.Equal(expected: 1, storeResolutions.Value, "five names must open exactly one scope");
        await store.Received(1).ListAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task TryResolveManyAsync_WhenToolIsDisabledUnacknowledgedOrInvalidSchema_OmitsIt()
    {
        // The batch applies IsOfferable and TryBuildSchema per candidate exactly as the per-name path did: a disabled,
        // unacknowledged, or unparsable tool stays dark, and the survivors still cost only the one read.
        var store = BuildStore([
            Record("custom__offerable"),
            Record("custom__disabled", enabled: false),
            Record("custom__unacknowledged", acknowledged: false),
            Record("custom__unparsable", parametersJson: "{ this is not json")
        ]);
        var storeResolutions = new ReadCounter();
        var catalog = CreateCatalog(store, storeResolutions, Settings(customToolsEnabled: true));

        var resolved = await catalog.TryResolveManyAsync([
            "custom__offerable",
            "custom__disabled",
            "custom__unacknowledged",
            "custom__unparsable"
        ], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, resolved.Count, "only the enabled, acknowledged, compilable tool may resolve");
        AssertEx.True(resolved.ContainsKey("custom__offerable"), "the offerable tool must be present");
        AssertEx.False(resolved.ContainsKey("custom__disabled"), "a disabled tool must never resolve");
        AssertEx.False(resolved.ContainsKey("custom__unacknowledged"), "an unacknowledged tool must never resolve");
        AssertEx.False(resolved.ContainsKey("custom__unparsable"), "a tool whose schema does not compile must never resolve");
        AssertEx.Equal(expected: 1, storeResolutions.Value, "the rejected candidates must not cost extra reads");
    }

    [Test]
    public async Task TryResolveManyAsync_ForAnUnknownOrBlankName_OmitsItWithoutThrowing()
    {
        // The per-name ArgumentException.ThrowIfNullOrWhiteSpace is gone with the batch, so a blank entry must be inert
        // rather than fatal: if the caller's custom__ prefix filter is ever loosened, this fails closed, not open.
        var store = BuildStore([Record("custom__known")]);
        var catalog = CreateCatalog(store, new ReadCounter(), Settings(customToolsEnabled: true));

        var resolved = await catalog.TryResolveManyAsync([
            "custom__known",
            "custom__nobody_has_this_name",
            "   "
        ], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, resolved.Count, "only the stored name may resolve");
        AssertEx.True(resolved.ContainsKey("custom__known"), "the stored name must resolve");
        AssertEx.False(resolved.ContainsKey("custom__nobody_has_this_name"), "an unknown name must be an absent key");
        AssertEx.False(resolved.ContainsKey("   "), "a blank name must be an absent key");
    }

    [Test]
    public async Task TryResolveManyAsync_ReturnsApprovalWrappedExecutables()
    {
        // The authoritative approval floor: every executable leaves the catalog already wrapped, so no downstream path
        // can obtain an ungated custom tool.
        var store = BuildStore([Record("custom__alpha"), Record("custom__bravo")]);
        var catalog = CreateCatalog(store, new ReadCounter(), Settings(customToolsEnabled: true));

        var resolved = await catalog.TryResolveManyAsync(["custom__alpha", "custom__bravo"], CancellationToken.None)
                                    .ConfigureAwait(false);

        AssertEx.Equal(expected: 2, resolved.Count);
        foreach (var (name, tool) in resolved)
        {
            AssertEx.True(tool is ApprovalRequiredAIFunction, $"{name} must be approval-wrapped by the catalog");
        }
    }

    [Test]
    public async Task TryResolveManyAsync_WhenCustomToolsAreDisabledAtTheNode_ReturnsEmptyWithoutReadingTheStore()
    {
        // The node kill-switch is the execution-time gate behind the offer merge: off means nothing resolves, and the
        // store is never touched — now checked once for the whole batch instead of once per name.
        var store = BuildStore([Record("custom__alpha")]);
        var storeResolutions = new ReadCounter();
        var catalog = CreateCatalog(store, storeResolutions, Settings(customToolsEnabled: false));

        var resolved = await catalog.TryResolveManyAsync(["custom__alpha"], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Empty(resolved, "a disabled node must resolve nothing");
        AssertEx.Equal(expected: 0, storeResolutions.Value, "a disabled node must open no scope");
        await store.DidNotReceive().ListAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task TryResolveManyAsync_WithNoNames_DoesNotTouchTheStore()
    {
        // Nothing requested must cost nothing at all: neither the settings read nor the store read.
        var store = BuildStore([Record("custom__alpha")]);
        var settings = Settings(customToolsEnabled: true);
        var storeResolutions = new ReadCounter();
        var catalog = CreateCatalog(store, storeResolutions, settings);

        var resolved = await catalog.TryResolveManyAsync([], CancellationToken.None).ConfigureAwait(false);

        AssertEx.Empty(resolved, "an empty request must resolve nothing");
        AssertEx.Equal(expected: 0, storeResolutions.Value, "an empty request must open no scope");
        await store.DidNotReceive().ListAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await settings.DidNotReceive().GetCustomToolsEnabledAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task TryResolveManyAsync_OnASecondCall_ReadsTheStoreAgainAndSeesTheEdit()
    {
        // The batch bounds the reads INSIDE one resolution; it is not a cache across resolutions. An operator CRUD edit
        // must take effect on the next turn with no invalidation hook, which is only true while every call re-reads.
        var records = new List<CustomToolRecord>
        {
            Record("custom__alpha")
        };
        var store = Substitute.For<ICustomToolStore>();
        store.ListAsync(Arg.Any<CancellationToken>())
             .Returns(_ => Task.FromResult<IReadOnlyList<CustomToolRecord>>([.. records]));
        var storeResolutions = new ReadCounter();
        var catalog = CreateCatalog(store, storeResolutions, Settings(customToolsEnabled: true));
        string[] requested = ["custom__alpha", "custom__bravo"];

        var first = await catalog.TryResolveManyAsync(requested, CancellationToken.None).ConfigureAwait(false);
        records.Add(Record("custom__bravo"));
        var second = await catalog.TryResolveManyAsync(requested, CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(first.ContainsKey("custom__bravo"), "the tool did not exist yet on the first call");
        AssertEx.True(second.ContainsKey("custom__bravo"), "a tool added between two calls must resolve on the second");
        AssertEx.Equal(expected: 2, storeResolutions.Value, "two resolutions must open two scopes, never share one read");
        await store.Received(2).ListAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task TryResolveManyAsync_WhenADuplicateNameIsStored_LetsTheFirstOfferableRecordClaimIt()
    {
        // The claimed-set semantic, which nothing else pins: the FIRST offerable record for a name owns it in store
        // order, so a later duplicate is never tried even when the owner then fails its schema check. Moving claimed.Add
        // below the executor/schema checks — the natural "cleanup" — would silently let the second row's configuration
        // execute instead, which is a different HTTP call under the same tool name.
        var store = BuildStore([
            Record("custom__dup", parametersJson: "{ not json"),
            Record("custom__dup")
        ]);
        var storeResolutions = new ReadCounter();
        var catalog = CreateCatalog(store, storeResolutions, Settings(customToolsEnabled: true));

        var resolved = await catalog.TryResolveManyAsync(["custom__dup"], CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(resolved.ContainsKey("custom__dup"),
            "the first offerable record claimed the name and then failed its schema, so the name must resolve to nothing");
        AssertEx.Empty(resolved, "the duplicate must not resolve under any other key either");
        AssertEx.Equal(expected: 1, storeResolutions.Value, "a duplicate name must not cost a second read");
        await store.Received(1).ListAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);

        // Control: the second record is valid on its own, so the absence above is the claim order and not an unresolvable
        // second row.
        var soloStore = BuildStore([Record("custom__dup")]);
        var soloCatalog = CreateCatalog(soloStore, new ReadCounter(), Settings(customToolsEnabled: true));

        var solo = await soloCatalog.TryResolveManyAsync(["custom__dup"], CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(solo.ContainsKey("custom__dup"), "the second record resolves when no earlier record claims the name");
    }

    private static CustomToolCatalog CreateCatalog(ICustomToolStore store,
        ReadCounter storeResolutions,
        INodeRuntimeSettings settings)
    {
        // A REAL scope factory over a substituted store, the seam the neighbouring store-backed tests use. The catalog
        // resolves ICustomToolStore exactly once per scope it opens, so the scoped factory delegate counts the scopes.
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            storeResolutions.Value++;
            return store;
        });

        return new CustomToolCatalog(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            settings,
            [new FakeHttpFetchExecutor()],
            Options.Create(new AgentToolPipelineOptions()),
            NullLogger<CustomToolCatalog>.Instance);
    }

    private static INodeRuntimeSettings Settings(bool customToolsEnabled)
    {
        return StubNodeRuntimeSettings.Create().WithCustomToolsEnabled(customToolsEnabled).Build();
    }

    private static ICustomToolStore BuildStore(IReadOnlyList<CustomToolRecord> records)
    {
        var store = Substitute.For<ICustomToolStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(records));
        return store;
    }

    private static CustomToolRecord Record(string name,
        bool enabled = true,
        bool acknowledged = true,
        string parametersJson = "[]")
    {
        return new CustomToolRecord(Guid.NewGuid(),
            name,
            $"Description of {name}",
            CustomToolKind.HttpFetch,
            CustomToolMode.Fixed,
            parametersJson,
            ConfigJson: "{}",
            enabled,
            acknowledged,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    // Resolution silently drops a record whose Kind has no registered executor, so the seeded HttpFetch kind needs one.
    // It is never invoked: these tests resolve executables, they do not run them.
    private sealed class FakeHttpFetchExecutor : ICustomToolExecutor
    {
        public CustomToolKind Kind => CustomToolKind.HttpFetch;

        public Task<string> ExecuteAsync(CustomToolRecord tool, string jsonArguments, CancellationToken cancellationToken)
        {
            return Task.FromResult("never invoked by these tests");
        }
    }

    // A mutable counter the scoped store factory can bump. It crosses a method boundary into the helper below, so
    // it has to be a reference type rather than a captured local int.
    private sealed class ReadCounter
    {
        public int Value { get; set; }
    }
}
