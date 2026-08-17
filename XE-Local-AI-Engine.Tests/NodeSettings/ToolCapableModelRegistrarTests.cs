namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ToolCapableModelRegistrar" />: feeds the template-detected tool capability of installed GGUF models
///     into the persisted <c>AgentHome:ToolCapableModels</c> allow-list that gates tool calling.
/// </summary>
/// <remarks>
///     REGRESSION: the shipped allow-list named two
///     previous-generation models (<c>qwen3:8b</c>, a Qwen2.5-3B GGUF). None of the current models the advisor ranks and
///     downloads appeared in it — including tool-capable ones — so a user who followed the app's OWN recommendation got
///     no tool calling and no explanation. The capability was already detected by <c>GgufCapabilityDetector</c> and
///     already persisted as <see cref="LocalModelDescriptor.IsToolCapable" />; nothing consumed it.
/// </remarks>
public sealed class ToolCapableModelRegistrarTests
{
    private const string ToolCapableGguf = "unsloth/Qwen3.6-27B-MTP-GGUF:Q4_K_M";
    private const string PlainGguf = "some/Plain-Chat-GGUF:Q4_K_M";

    [Test]
    public async Task RegisterIfToolCapable_WhenModelAdvertisesTools_AddsItToTheAllowList()
    {
        var store = NewSettingsStore(existing: ["qwen3:8b"]);
        var registrar = NewRegistrar(store, Descriptor(ToolCapableGguf, isToolCapable: true));

        var added = await registrar.RegisterIfToolCapableAsync(ToolCapableGguf, CancellationToken.None);

        AssertEx.True(added, "A tool-capable model should be admitted to the allow-list.");
        var saved = CapturedSave(store);
        AssertEx.Contains(saved.ToolCapableModels!, ToolCapableGguf);

        // Additive only: an operator-curated entry must survive.
        AssertEx.Contains(saved.ToolCapableModels!, "qwen3:8b");
    }

    [Test]
    public async Task RegisterIfToolCapable_WhenModelDoesNotAdvertiseTools_DoesNotAddIt()
    {
        // Detection must only ever GRANT capability here. A plain chat template stays out, so the gate keeps denying
        // high-risk tools (run_in_agent_home + every MCP tool) to a model that cannot drive them.
        var store = NewSettingsStore(existing: ["qwen3:8b"]);
        var registrar = NewRegistrar(store, Descriptor(PlainGguf, isToolCapable: false));

        var added = await registrar.RegisterIfToolCapableAsync(PlainGguf, CancellationToken.None);

        AssertEx.False(added, "A model whose template advertises no tools must not be admitted.");
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterIfToolCapable_WhenAlreadyListed_DoesNotRewriteSettings()
    {
        // SaveAsync invalidates and re-primes the settings cache and is reached on every completed download, so a
        // no-op must not churn it.
        var store = NewSettingsStore(existing: [ToolCapableGguf]);
        var registrar = NewRegistrar(store, Descriptor(ToolCapableGguf, isToolCapable: true));

        var added = await registrar.RegisterIfToolCapableAsync(ToolCapableGguf, CancellationToken.None);

        AssertEx.False(added);
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterIfToolCapable_WhenModelIsNotInstalled_DoesNothing()
    {
        // A cloud/Ollama model has no GGUF descriptor at all; it must not be admitted by this path.
        var store = NewSettingsStore(existing: []);
        var registrar = NewRegistrar(store, Descriptor(PlainGguf, isToolCapable: true));

        var added = await registrar.RegisterIfToolCapableAsync("gpt-5-cloud", CancellationToken.None);

        AssertEx.False(added);
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Backfill_AddsEveryInstalledToolCapableModel_AndLeavesTheRestAlone()
    {
        // Without the backfill the fix would only apply to FUTURE downloads, leaving every already-installed model
        // silently tool-less — which is exactly the state the capture run found.
        var store = NewSettingsStore(existing: ["qwen3:8b"]);
        var registrar = NewRegistrar(store,
            Descriptor(ToolCapableGguf, isToolCapable: true),
            Descriptor(PlainGguf, isToolCapable: false),
            Descriptor("another/Tooling-GGUF:Q4_K_M", isToolCapable: true));

        var added = await registrar.BackfillInstalledAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, added);
        var saved = CapturedSave(store);
        AssertEx.Contains(saved.ToolCapableModels!, ToolCapableGguf);
        AssertEx.Contains(saved.ToolCapableModels!, "another/Tooling-GGUF:Q4_K_M");
        AssertEx.Contains(saved.ToolCapableModels!, "qwen3:8b");
        AssertEx.False(saved.ToolCapableModels!.Contains(PlainGguf), "A non-tool-capable model must not be backfilled.");
    }

    [Test]
    public async Task Backfill_WhenEverythingIsAlreadyListed_DoesNotRewriteSettings()
    {
        // The backfill runs on EVERY startup, so a steady-state node must not rewrite the settings file each boot.
        var store = NewSettingsStore(existing: [ToolCapableGguf]);
        var registrar = NewRegistrar(store, Descriptor(ToolCapableGguf, isToolCapable: true));

        var added = await registrar.BackfillInstalledAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, added);
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterIfToolCapable_StoresTheDescriptorsExactCasing()
    {
        // The gate compares Ordinal (case-SENSITIVE), so an entry stored in the caller's casing would not match the
        // model id the chat path presents. Presence is checked case-insensitively; the value stored is the descriptor's.
        var store = NewSettingsStore(existing: []);
        var registrar = NewRegistrar(store, Descriptor(ToolCapableGguf, isToolCapable: true));

        var added = await registrar.RegisterIfToolCapableAsync(ToolCapableGguf.ToUpperInvariant(), CancellationToken.None);

        AssertEx.True(added);
        AssertEx.Contains(CapturedSave(store).ToolCapableModels!, ToolCapableGguf);
    }

    private static ToolCapableModelRegistrar NewRegistrar(INodeSettingsStore store, params LocalModelDescriptor[] installed)
    {
        var ggufStore = Substitute.For<IGgufModelStore>();
        ggufStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(installed));

        return new ToolCapableModelRegistrar(ggufStore, store, NullLogger<ToolCapableModelRegistrar>.Instance);
    }

    private static INodeSettingsStore NewSettingsStore(IReadOnlyList<string> existing)
    {
        var store = Substitute.For<INodeSettingsStore>();
        var stored = new StoredNodeSettings
        {
            ToolCapableModels = existing
        };
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(stored));
        store.Load(Arg.Any<CancellationToken>()).Returns(stored);
        return store;
    }

    private static StoredNodeSettings CapturedSave(INodeSettingsStore store)
    {
        var calls = store.ReceivedCalls()
                         .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(INodeSettingsStore.SaveAsync), StringComparison.Ordinal))
                         .ToArray();

        AssertEx.Equal(expected: 1, calls.Length);
        return (StoredNodeSettings)calls[0].GetArguments()[0]!;
    }

    private static LocalModelDescriptor Descriptor(string modelName, bool isToolCapable) =>
        new()
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = null,
            ModifiedAt = null,
            MaxContextTokens = null,
            IsToolCapable = isToolCapable
        };
}
