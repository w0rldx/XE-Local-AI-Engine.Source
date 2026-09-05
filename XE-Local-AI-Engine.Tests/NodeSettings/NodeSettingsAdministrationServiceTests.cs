namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeSettingsAdministrationServiceTests
{
    [Test]
    public async Task ApplyAgenticPatchAsync_ChangesApprovedFieldsAndPreservesExcludedFields()
    {
        var store = Substitute.For<INodeSettingsStore>();
        var current = new StoredNodeSettings
        {
            DefaultModelName = "old",
            CustomToolsEnabled = true,
            OllamaEndpoint = "http://127.0.0.1:11434",
            MaxResponseSizeMb = 42,
            VoiceFeatureEnabled = true,
            ToolApprovalPolicy = new NodeToolApprovalPolicySettings()
        };
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(current);
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = " new ",
            EnableTools = false,
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated, "a valid agentic patch must be saved.");
        AssertEx.Equal("new", result.Settings.DefaultModelName);
        AssertEx.Equal(false, result.Settings.EnableTools);
        AssertEx.Equal(512, result.Settings.ChatCacheReuse);
        AssertEx.Equal(true, result.Settings.CustomToolsEnabled);
        AssertEx.Equal("http://127.0.0.1:11434", result.Settings.OllamaEndpoint);
        AssertEx.Equal(42, result.Settings.MaxResponseSizeMb);
        AssertEx.Equal(true, result.Settings.VoiceFeatureEnabled);
        AssertEx.NotNull(result.Settings.ToolApprovalPolicy);
        await store.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate).CustomToolsEnabled == current.CustomToolsEnabled
                && Persisted(mutate).OllamaEndpoint == current.OllamaEndpoint
                && Persisted(mutate).MaxResponseSizeMb == current.MaxResponseSizeMb
                && Persisted(mutate).VoiceFeatureEnabled == current.VoiceFeatureEnabled
                && ReferenceEquals(Persisted(mutate).ToolApprovalPolicy, current.ToolApprovalPolicy)),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public void AgenticPatch_IsStructurallyLimitedToApprovedFields()
    {
        var names = typeof(NodeSettingsAgenticPatch).GetProperties().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        string[] approved =
        [
            "DefaultModelName", "EnableTools", "ToolCapableModels", "HuggingFaceDefaultQuant",
            "LlamaMaxLoadedProcesses", "LlamaIdleTimeToLiveSeconds", "KeepModelWarmEnabled",
            "KeepModelWarmModelName", "KeepModelWarmIntervalSeconds", "MaxMessageRequestTimeoutSeconds",
            "ChatCacheReuse", "SpeculativeMode", "SpeculativeDraftModelName", "SpeculativeDraftMaxTokens",
            "SpeculativeDraftGpuLayers", "KvCacheType", "RerankerModelName", "AutoEffortFastModelName"
        ];

        AssertEx.Equal(approved.Length, names.Count);
        AssertEx.True(names.SetEquals(approved), "the agentic patch must expose exactly the approved 18 fields.");
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.CustomToolsEnabled)));
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.ToolApprovalPolicy)));
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.OllamaEndpoint)));
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelIsExternal_IsRejected()
    {
        // The fast model may be moved a turn's whole context onto, and that context was admitted upstream against a
        // node-local model. An external server is a process this node does not own, so the setting is refused before
        // it can ever be stored — the same pair the dispatcher re-checks per turn.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult("external"));
        var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "ext:studio/qwen3-1.7b"
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelIsCloud_IsRejected()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ModelTrustLocality.Cloud));
        var service = CreateService(store, modelTrustResolver: trustResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "gpt-5.6-terra"
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelIsNotInstalled_IsRejected()
    {
        // The hole the live round found: with no cloud provider configured, `ModelTrustResolver` classifies a
        // scheme-less id as Local and `LocalModelProviderResolver` routes an unmapped id to the default provider
        // (llamacpp), so the pair alone accepted ANY string — a cloud model id saved with HTTP 200. Registry
        // membership is what refuses it.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var service = CreateService(store, fastModelInstalled: false);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "gpt-4o-mini"
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelIsServedByOllama_IsRejected()
    {
        // Node-local is not enough. The swap targets a llama.cpp process — the only provider the capacity gate and the
        // liveness probe can reason about — so an Ollama-served local model is refused at the same point.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult("ollama"));
        var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "qwen3:1.7b"
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Save_WhenMaxLoadedProcessesIsOne_IsRejected()
    {
        // The fast model is a SECOND chat process alongside the conversation's own, so a node capped at one slot could
        // never admit it: the setting would look configured and silently never apply.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "qwen3-1.7b",
            LlamaMaxLoadedProcesses = 1
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.LlamaMaxLoadedProcesses, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelNotProvided_LeavesStoredSettingsUnchanged()
    {
        // The shipped default. A patch that never mentions the setting must neither set it nor pay for the locality
        // lookup that guards it.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            AutoEffortFastModelName = "qwen3-1.7b"
        });
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ModelTrustLocality.Local));
        var service = CreateService(store, modelTrustResolver: trustResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("qwen3-1.7b", result.Settings.AutoEffortFastModelName);
        await trustResolver.DidNotReceiveWithAnyArgs().ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Save_WhenTheStoredFastModelIsNoLongerLocal_DoesNotBlockAnUnrelatedPatch()
    {
        // The guard validates a CHANGE, not the merged result. Uninstalling the configured fast model must not turn
        // every save of every other setting into a rejection naming a field the operator never touched; the
        // dispatcher's per-turn re-check is what keeps the stale value from ever being used.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            AutoEffortFastModelName = "qwen3-1.7b"
        });
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult("external"));
        var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated, "an unrelated patch must not be rejected over a stored fast model.");
        AssertEx.Equal("qwen3-1.7b", result.Settings.AutoEffortFastModelName);
        await store.Received(1).UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SaveTrustedMerged_WhenTheStoredFastModelIsNoLongerLocal_SavesTheUnchangedValue()
    {
        // The same rule on the endpoint's path, which hands the service an already-merged snapshot: the fast model
        // rides along unchanged in every save, so re-validating it there would block the settings page outright.
        var store = Substitute.For<INodeSettingsStore>();
        var stored = new StoredNodeSettings
        {
            AutoEffortFastModelName = "qwen3-1.7b"
        };
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(stored);
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult("external"));
        var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.SaveTrustedMergedAsync(stored with { ChatCacheReuse = 512 }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("qwen3-1.7b", result.Settings.AutoEffortFastModelName);
        await store.Received(1).UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SaveTrustedMerged_WhenTheIncomingRecordHasNoMachineKey_PreservesTheStoredOne()
    {
        // The wire DTO cannot carry MachineKey, so the endpoint's merged record always arrives without one. Saving it
        // verbatim orphaned every frozen inference profile after the next restart minted a fresh key.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            MachineKey = "abc"
        });
        var service = CreateService(store);

        var result = await service.SaveTrustedMergedAsync(new StoredNodeSettings
        {
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("abc", result.Settings.MachineKey);
        await store.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate, new StoredNodeSettings { MachineKey = "abc" }).MachineKey == "abc"),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SaveTrustedMerged_WhenTheMachineKeyIsMintedBetweenTheLoadAndTheWrite_PersistsTheMintedOne()
    {
        // The save reads the settings, validates, and only then writes; IMachineKeyProvider mints on the same node and
        // can land inside that window. A whole-file save built from the record this service LOADED would write the
        // null the request carried back over the fresh key — orphaning every frozen inference profile silently. The
        // guard is that the write happens as a read-modify-write under the store's lock, so the mutation sees the
        // record as it is AT WRITE TIME.
        var store = new InterleavingNodeSettingsStore(new StoredNodeSettings(),
            mintedBeforeTheWrite: "minted-while-the-save-was-validating");
        var service = CreateService(store);

        var result = await service.SaveTrustedMergedAsync(new StoredNodeSettings
        {
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("minted-while-the-save-was-validating", store.Current.MachineKey);
        AssertEx.Equal(expected: 512, store.Current.ChatCacheReuse, "the operator's change still lands.");
        AssertEx.Equal("minted-while-the-save-was-validating", result.Settings.MachineKey,
            "and the caller is told the key that is actually stored, not the one it loaded.");
    }

    [Test]
    public async Task SaveTrustedMerged_WhenTheFastModelChangesToANonLocalOne_IsRejected()
    {
        // The change itself is still refused on the endpoint's path, not only the MCP patch's.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult("external"));
        var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.SaveTrustedMergedAsync(new StoredNodeSettings
        {
            AutoEffortFastModelName = "ext:studio/qwen3-1.7b"
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenMergedPolicyRejects_DoesNotSave()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            KeepModelWarmEnabled = true,
            KeepModelWarmModelName = " "
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.KeepModelWarmModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenFieldRangeIsInvalid_DoesNotNormalizeSaveOrReport()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var reporter = Substitute.For<ICapabilityReporter>();
        var service = CreateService(store, reporter: reporter);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = StoredNodeSettings.MaxChatCacheReuse + 1
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(NodeSettingsField.ChatCacheReuse, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await reporter.DidNotReceive().ReportToApiAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenToolCapableModelsIsEmpty_DoesNotSave()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ToolCapableModels = []
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(NodeSettingsField.ToolCapableModels, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_DefaultModelCloudTransition_UsesSharedCacheInvalidationPolicy()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            DefaultModelName = "old-cloud"
        });
        var cloudResolver = Substitute.For<ICloudModelResolver>();
        cloudResolver.IsCloudModelAsync("old-cloud", Arg.Any<CancellationToken>()).Returns(true);
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        var service = CreateService(store, cloudResolver: cloudResolver, cloudFactory: cloudFactory);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = "local-model"
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("local-model", result.Settings.DefaultModelName);
        cloudFactory.Received(1).InvalidateSelectionCache();
    }

    private static NodeSettingsAdministrationService CreateService(INodeSettingsStore store,
        ICapabilityReporter? reporter = null,
        ICloudModelResolver? cloudResolver = null,
        IActiveCloudChatClientFactory? cloudFactory = null,
        INodeRuntimeSettings? runtimeSettings = null,
        IModelTrustResolver? modelTrustResolver = null,
        ILocalModelProviderResolver? localModelProviderResolver = null,
        bool fastModelInstalled = true)
    {
        var runtime = runtimeSettings ?? Substitute.For<INodeRuntimeSettings>();
        runtime.GetLlamaMaxLoadedProcessesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(StoredNodeSettings.DefaultLlamaMaxLoadedProcesses));

        // Default to an installed node-local llama.cpp model so the fast-model locality gate is transparent to every
        // test that does not set the setting at all. The trust stub is explicit rather than leaning on NSubstitute's
        // default, which only happens to be Local because Local is the enum's zero.
        if (modelTrustResolver is null)
        {
            modelTrustResolver = Substitute.For<IModelTrustResolver>();
            modelTrustResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                              .Returns(Task.FromResult(ModelTrustLocality.Local));
        }

        if (localModelProviderResolver is null)
        {
            localModelProviderResolver = Substitute.For<ILocalModelProviderResolver>();
            localModelProviderResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                                      .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        }

        reporter ??= Substitute.For<ICapabilityReporter>();
        reporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        cloudResolver ??= Substitute.For<ICloudModelResolver>();
        cloudFactory ??= Substitute.For<IActiveCloudChatClientFactory>();

        // Registry membership is the third point of the fast-model gate, and the only one that refuses an id neither
        // resolver has ever heard of: both of them default an unknown name to node-local llama.cpp.
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        ggufModelStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(fastModelInstalled));

        var selectionPolicy = new DefaultModelSelectionPolicy(ggufModelStore,
            cloudResolver,
            cloudFactory,
            new ModelNameValidator(Options.Create(new SecurityOptions())));
        return new NodeSettingsAdministrationService(store,
            runtime,
            reporter,
            selectionPolicy,
            ggufModelStore,
            modelTrustResolver,
            localModelProviderResolver,
            NullLogger<NodeSettingsAdministrationService>.Instance);
    }

    /// <summary>
    ///     The record a save actually persists. The service writes through <see cref="INodeSettingsStore.UpdateAsync" />,
    ///     so what lands is its mutation applied to the settings the store holds AT WRITE TIME — which is not
    ///     necessarily what the service loaded.
    /// </summary>
    private static StoredNodeSettings Persisted(Func<StoredNodeSettings, StoredNodeSettings> mutate, StoredNodeSettings? latest = null) =>
        mutate(latest ?? new StoredNodeSettings());

    /// <summary>
    ///     A store whose stored record gains a machine key AFTER the service has loaded it and before the write — the
    ///     interleaving <c>IMachineKeyProvider</c> produces against a settings save on the same node.
    /// </summary>
    private sealed class InterleavingNodeSettingsStore(StoredNodeSettings initial, string mintedBeforeTheWrite) : INodeSettingsStore
    {
        public StoredNodeSettings Current { get; private set; } = initial;

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public StoredNodeSettings Load(CancellationToken cancellationToken = default) =>
            Current;

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }

        public Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(mutate);

            // The sibling writer got here first: the mutation must be applied to THIS record, not to the one the
            // caller read before it existed.
            Current = Current with
            {
                MachineKey = mintedBeforeTheWrite
            };
            Current = mutate(Current);
            return Task.FromResult(Current);
        }
    }
}
