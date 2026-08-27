namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The reconciliation pass that keeps the provider map, the tool-capable allow-list and the node default in step
///     with the encrypted store — the self-healing that makes a save's three unsynchronized writes survivable.
/// </summary>
/// <remarks>
///     The defining property under test is CONTAINMENT: the pass runs unconditionally on every boot, so every
///     assertion here also asks what it left alone. A GGUF's map row, an Ollama backfill row and a hand-curated
///     allow-list entry for a local model must all come through it untouched.
/// </remarks>
public sealed class ExternalProviderReconcilerTests
{
    [Test]
    public async Task ReconcileAsync_WritesAMapRowPerRegisteredModel()
    {
        var fixture = new Fixture();
        fixture.Registry.Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());

        var report = await fixture.Reconciler.ReconcileAsync();

        AssertEx.Equal(1, report.MapRowsWritten);
        AssertEx.Equal("external", fixture.MapStore.Mappings[ExternalProviderTestData.ModelId].ProviderName);
    }

    [Test]
    public async Task ReconcileAsync_WhenTheMapIsAlreadyCorrect_ChangesNothing()
    {
        var fixture = new Fixture();
        fixture.Registry.Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        fixture.MapStore.Seed(ExternalProviderTestData.ModelId, "external");

        var report = await fixture.Reconciler.ReconcileAsync();

        AssertEx.False(report.Changed);
        fixture.ProviderResolver.DidNotReceive().InvalidateModelProviderMap();
    }

    [Test]
    public async Task ReconcileAsync_RemovesAnOrphanedExternalRow()
    {
        var fixture = new Fixture();
        fixture.MapStore.Seed("ext:deleted-box/qwen3", "external");

        var report = await fixture.Reconciler.ReconcileAsync();

        AssertEx.Equal(1, report.MapRowsRemoved);
        AssertEx.Empty(fixture.MapStore.Mappings);
    }

    [Test]
    public async Task ReconcileAsync_LeavesNonExternalRowsAlone()
    {
        var fixture = new Fixture();
        fixture.MapStore.Seed("qwen3-27b.gguf", LlamaServerProviderConstants.ProviderName);
        fixture.MapStore.Seed("qwen3:8b", "ollama");

        var report = await fixture.Reconciler.ReconcileAsync();

        // The pass runs on every boot; a GGUF row or an Ollama backfill row it touched would be a data-loss bug.
        AssertEx.False(report.Changed);
        AssertEx.Equal(2, fixture.MapStore.Mappings.Count);
    }

    [Test]
    public async Task ReconcileAsync_KeepsARowSpelledWithADifferentCase()
    {
        var fixture = new Fixture();
        fixture.Registry.Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        fixture.MapStore.Seed(ExternalProviderTestData.ModelId.Replace("unsloth-box", "UNSLOTH-BOX", StringComparison.Ordinal), "external");

        var report = await fixture.Reconciler.ReconcileAsync();

        // The map key is NOCASE, so a hand-edited row IS this model's row: recognizing it is what stops the pass from
        // deleting a live route and inserting a duplicate.
        AssertEx.False(report.Changed);
    }

    [Test]
    public async Task ReconcileAsync_AddsToolCapableModelsToTheAllowList()
    {
        var fixture = new Fixture(existingAllowList: ["qwen3:8b"]);
        fixture.Registry.Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(supportsTools: true));

        var report = await fixture.Reconciler.ReconcileAsync();

        AssertEx.Equal(1, report.AllowListAdded);
        var saved = fixture.CapturedSettings();
        AssertEx.Contains(saved.ToolCapableModels!, ExternalProviderTestData.ModelId);
        // Additive for everything that is not an ext: id — the list is operator-curated.
        AssertEx.Contains(saved.ToolCapableModels!, "qwen3:8b");
    }

    [Test]
    public async Task ReconcileAsync_DoesNotAddAModelThatDeclaresNoTools()
    {
        var fixture = new Fixture();
        fixture.Registry.Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(supportsTools: false));

        var report = await fixture.Reconciler.ReconcileAsync();

        AssertEx.Equal(0, report.AllowListAdded);
    }

    [Test]
    public async Task ReconcileAsync_RemovesOnlyExternalAllowListEntriesOnUnregister()
    {
        var fixture = new Fixture(existingAllowList: ["qwen3:8b", "ext:deleted-box/qwen3", "some/Local-GGUF:Q4_K_M"]);

        var report = await fixture.Reconciler.ReconcileAsync();

        AssertEx.Equal(1, report.AllowListRemoved);
        var saved = fixture.CapturedSettings();
        AssertEx.Equal(2, saved.ToolCapableModels!.Count);
        AssertEx.Contains(saved.ToolCapableModels, "qwen3:8b");
        AssertEx.Contains(saved.ToolCapableModels, "some/Local-GGUF:Q4_K_M");
    }

    [Test]
    public async Task ReconcileAsync_ClearsADanglingExternalDefaultModel()
    {
        var fixture = new Fixture(defaultModelName: "ext:deleted-box/qwen3");

        var report = await fixture.Reconciler.ReconcileAsync();

        // A crash between "connection deleted" and "default cleared" leaves a selection that routes nowhere; this is
        // what makes it self-heal on the next boot.
        AssertEx.True(report.DefaultModelCleared);
        AssertEx.Null(fixture.CapturedSettings().DefaultModelName);
    }

    [Test]
    public async Task ReconcileAsync_KeepsAStillRegisteredExternalDefaultModel()
    {
        var fixture = new Fixture(defaultModelName: ExternalProviderTestData.ModelId);
        fixture.Registry.Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());

        var report = await fixture.Reconciler.ReconcileAsync();

        AssertEx.False(report.DefaultModelCleared);
    }

    [Test]
    public async Task ReconcileAsync_KeepsALocalDefaultModel()
    {
        var fixture = new Fixture(defaultModelName: "qwen3-27b.gguf");

        var report = await fixture.Reconciler.ReconcileAsync();

        AssertEx.False(report.DefaultModelCleared);
        await fixture.SettingsStore.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReconcileAsync_WhenItRepairsDrift_DropsBothRoutingCaches()
    {
        var fixture = new Fixture();
        fixture.Registry.Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());

        _ = await fixture.Reconciler.ReconcileAsync();

        // The resolver memoizes model-to-provider for seconds and the router caches a chat client per provider and
        // model, so a repaired row neither of them sees is a repair that has not taken effect.
        fixture.ProviderResolver.Received(1).InvalidateModelProviderMap();
        fixture.ChatClientCache.Received(1).ClearClientCache();
    }

    private sealed class Fixture
    {
        public Fixture(IReadOnlyList<string>? existingAllowList = null, string? defaultModelName = null)
        {
            var stored = new StoredNodeSettings
            {
                ToolCapableModels = existingAllowList,
                DefaultModelName = defaultModelName
            };
            _ = SettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(stored));

            Reconciler = new ExternalProviderReconciler(Registry,
                MapStore,
                new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain()),
                ProviderResolver,
                ChatClientCache,
                SettingsStore,
                NullLogger<ExternalProviderReconciler>.Instance);
        }

        public FakeExternalProviderRegistry Registry { get; } = new();
        public InMemoryCoordinatedModelProviderMapStore MapStore { get; } = new();
        public ILocalModelProviderResolver ProviderResolver { get; } = Substitute.For<ILocalModelProviderResolver>();
        public ILocalChatClientCacheInvalidator ChatClientCache { get; } = Substitute.For<ILocalChatClientCacheInvalidator>();
        public INodeSettingsStore SettingsStore { get; } = Substitute.For<INodeSettingsStore>();
        public ExternalProviderReconciler Reconciler { get; }

        public StoredNodeSettings CapturedSettings()
        {
            return (StoredNodeSettings)SettingsStore.ReceivedCalls()
                                                    .Single(call => string.Equals(call.GetMethodInfo().Name, nameof(INodeSettingsStore.SaveAsync), StringComparison.Ordinal))
                                                    .GetArguments()[0]!;
        }
    }
}
