namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeSettingsAdministrationServiceTests
{
    [Test]
    public async Task ApplyAgenticPatchAsync_ChangesApprovedFieldsAndPreservesExcludedFields()
    {
        // Asserted through the record the store actually HOLDS afterwards, not through the validation preview: a write
        // path that projected the patch onto the right record but then persisted a different one — dropping
        // DefaultModelName or EnableTools on the way to disk — passed an assertion made against the preview.
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            DefaultModelName = "old",
            CustomToolsEnabled = true,
            OllamaEndpoint = "http://127.0.0.1:11434",
            MaxResponseSizeMb = 42,
            VoiceFeatureEnabled = true,
            ToolApprovalPolicy = new NodeToolApprovalPolicySettings()
        });
        var approvalPolicy = store.Current.ToolApprovalPolicy;
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = " new ",
            EnableTools = false,
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated, "a valid agentic patch must be saved.");
        AssertEx.Equal(expected: 1, store.WriteCount, "an uncontended patch writes exactly once.");
        AssertEx.Equal("new", store.Current.DefaultModelName);
        AssertEx.Equal(false, store.Current.EnableTools);
        AssertEx.Equal(512, store.Current.ChatCacheReuse);
        // The excluded fields come from the write-time record, because that is where a partial patch takes every field
        // it does not name from.
        AssertEx.Equal(true, store.Current.CustomToolsEnabled);
        AssertEx.Equal("http://127.0.0.1:11434", store.Current.OllamaEndpoint);
        AssertEx.Equal(42, store.Current.MaxResponseSizeMb);
        AssertEx.Equal(true, store.Current.VoiceFeatureEnabled);
        AssertEx.True(ReferenceEquals(store.Current.ToolApprovalPolicy, approvalPolicy));
        AssertEx.Equal("new", result.Settings.DefaultModelName, "and the caller is told what was written.");
        AssertEx.Equal(false, result.Settings.EnableTools);
        AssertEx.Equal(512, result.Settings.ChatCacheReuse);
    }

    [Test]
    public async Task ApplyAgenticPatch_WhenASiblingWriterAppendsAToolCapableModelBetweenTheLoadAndTheWrite_KeepsItsEntry()
    {
        // The patch is PARTIAL, so every field it does not name must come from the record the write lands on. Building
        // the whole saved record from the snapshot loaded before validation wrote that snapshot's ToolCapableModels
        // back over the registrar's append, silently un-registering a model the operator had just made tool-capable.
        // One-shot: a sibling writer lands once. The service re-validates and retries when the record moved under it,
        // so a hook that fired on every write would model a writer that never stops rather than a single race.
        var siblingHasWritten = false;
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
            {
                ToolCapableModels = ["already-approved"]
            },
            siblingWriteBeforeTheUpdate: latest =>
            {
                if (siblingHasWritten)
                {
                    return latest;
                }

                siblingHasWritten = true;
                return latest with
                {
                    ToolCapableModels = [.. latest.ToolCapableModels ?? [], "registered-while-the-patch-validated"]
                };
            });
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal(expected: 512, store.Current.ChatCacheReuse, "the patched field still lands.");
        AssertEx.True(store.Current.ToolCapableModels?.Contains("registered-while-the-patch-validated") == true,
            "a sibling writer's registration must survive a patch that never names ToolCapableModels.");
        AssertEx.True(store.Current.ToolCapableModels?.Contains("already-approved") == true,
            "and the entries that were already stored stay.");
        AssertEx.Equal(expected: 512, result.Settings.ChatCacheReuse);
        AssertEx.True(result.Settings.ToolCapableModels?.Contains("registered-while-the-patch-validated") == true,
            "the caller is told what was actually written, not what it validated.");
    }

    [Test]
    public async Task SaveTrustedMerged_WhenASiblingAppendsAToolCapableModelBetweenTheLoadAndTheWrite_KeepsItsEntry()
    {
        // The HTTP save is a partial merge in disguise: the wire DTO's fields are all optional, so the endpoint's
        // mapper resolves every OMITTED one from the record it is handed. Handed a snapshot loaded before validation,
        // a request that changes only ChatCacheReuse wrote that snapshot's ToolCapableModels back over the registrar's
        // append — silently un-registering a model the operator had just made tool-capable.
        var siblingHasWritten = false;
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
            {
                ToolCapableModels = ["already-approved"]
            },
            siblingWriteBeforeTheUpdate: latest =>
            {
                if (siblingHasWritten)
                {
                    return latest;
                }

                siblingHasWritten = true;
                return latest with
                {
                    ToolCapableModels = [.. latest.ToolCapableModels ?? [], "registered-while-the-save-validated"]
                };
            });
        var service = CreateService(store);

        // Request-shaped: only ChatCacheReuse is supplied, every other field comes from the record this is applied to.
        var result = await service.SaveTrustedMergedAsync(static record => new StoredNodeSettings
        {
            ChatCacheReuse = 512,
            ToolCapableModels = record.ToolCapableModels,
            DefaultModelName = record.DefaultModelName
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal(expected: 512, store.Current.ChatCacheReuse, "the field the request changed still lands.");
        AssertEx.True(store.Current.ToolCapableModels?.Contains("registered-while-the-save-validated") == true,
            "a sibling registration must survive a save whose request never named ToolCapableModels.");
        AssertEx.True(store.Current.ToolCapableModels?.Contains("already-approved") == true,
            "and the entries that were already stored stay.");
        AssertEx.True(result.Settings.ToolCapableModels?.Contains("registered-while-the-save-validated") == true,
            "the caller is told what was actually written, not what it validated.");
    }

    [Test]
    public async Task ApplyAgenticPatch_WhenTheRecordKeepsChangingOnEveryAttempt_RefusesInsteadOfWritingUnvalidatedState()
    {
        // A writer that never stops. Every attempt finds the record moved, so no attempt ever validates the record its
        // write would land on. Spending the last attempt writing the projection anyway persisted state nothing had
        // validated — the exact composition NodeSettingsPolicy exists to keep off disk, reached by exhausting a retry.
        var siblingWrites = 0;
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
            {
                KeepModelWarmModelName = "warm-model",
                KeepModelWarmIntervalSeconds = 60,
                LlamaIdleTimeToLiveSeconds = 900,
                LlamaMaxLoadedProcesses = 4
            },
            siblingWriteBeforeTheUpdate: latest => latest with
            {
                ToolCapableModels = [.. latest.ToolCapableModels ?? [], $"sibling-{++siblingWrites}"]
            });
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            KeepModelWarmEnabled = true
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated, "a save that never validated the record it would land on must not report success.");
        AssertEx.True(result.Conflicted, "and the caller must be able to tell a conflict from a rejection.");
        AssertEx.Equal(expected: 0, result.ValidationErrors.Count, "nothing the caller sent was wrong.");
        AssertEx.True(store.Current.KeepModelWarmEnabled is not true, "the unvalidated patch must not reach disk.");
        // One no-op write per attempt: UpdateAsync is the only way to read the write-time record under the store's
        // lock and it always persists, so each attempt rewrites the record unchanged.
        AssertEx.Equal(expected: 3, store.WriteCount);
    }

    [Test]
    public async Task ApplyAgenticPatch_WhenASiblingChangesAValidatedFieldBetweenTheLoadAndTheWrite_RevalidatesAgainstTheWriteTimeRecord()
    {
        // Two individually valid updates must not compose into an invalid record. This patch validates "keep model
        // warm on" against the stored warm model, and a sibling clears that model in the window. Re-applying the
        // projection to the cleared record persists keep-warm ENABLED WITH NO MODEL SELECTED — precisely the state
        // NodeSettingsPolicy exists to keep off disk, reached without either writer ever proposing it.
        var siblingHasWritten = false;
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
            {
                KeepModelWarmModelName = "warm-model",
                KeepModelWarmIntervalSeconds = 60,
                LlamaIdleTimeToLiveSeconds = 900,
                LlamaMaxLoadedProcesses = 4
            },
            siblingWriteBeforeTheUpdate: latest =>
            {
                if (siblingHasWritten)
                {
                    return latest;
                }

                siblingHasWritten = true;
                return latest with
                {
                    KeepModelWarmModelName = null
                };
            });
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            KeepModelWarmEnabled = true
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated, "the re-validation against the write-time record must refuse the patch.");
        AssertEx.Equal(expected: 1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.KeepModelWarmModelName, result.ValidationErrors[0].Field);
        AssertEx.True(store.Current.KeepModelWarmEnabled is not true,
            "keep-warm must never reach disk enabled with no model selected.");
        // The retry is visible in the write count. The attempt that found the record moved still costs one write:
        // UpdateAsync is the only way to read the write-time record under the store's lock, and it always persists —
        // that write rewrites the record unchanged. The re-validation then rejects, so no second write follows.
        AssertEx.Equal(expected: 1, store.WriteCount);
    }

    [Test]
    public async Task ApplyAgenticPatch_WhenASiblingChangesTheDefaultModelBeforeTheWrite_InvalidatesTheTransitionItActuallyReplaced()
    {
        // The cloud-client cache is invalidated for a TRANSITION, so its "previous" has to be the value the write
        // actually replaced. Taken from the pre-validation snapshot it named a model that was already superseded, so a
        // transition off the sibling's cloud selection went unnoticed and the cached client stayed.
        var siblingHasWritten = false;
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
            {
                DefaultModelName = "snapshot-model"
            },
            siblingWriteBeforeTheUpdate: latest =>
            {
                if (siblingHasWritten)
                {
                    return latest;
                }

                siblingHasWritten = true;
                return latest with
                {
                    DefaultModelName = "sibling-picked-model"
                };
            });
        var cloudResolver = Substitute.For<ICloudModelResolver>();
        var service = CreateService(store, cloudResolver: cloudResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = "patched-model"
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("patched-model", store.Current.DefaultModelName);
        await cloudResolver.Received(1).IsCloudModelAsync("sibling-picked-model", Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await cloudResolver.DidNotReceive().IsCloudModelAsync("snapshot-model", Arg.Any<CancellationToken>()).ConfigureAwait(false);
        // The no-op write from the attempt that found the record moved, then the write the re-validated attempt made.
        AssertEx.Equal(expected: 2, store.WriteCount);
    }

    [Test]
    public async Task ApplyAgenticPatch_ReturnsTheRecordTheStorePersisted_NotTheOneTheMutationProduced()
    {
        // The store normalizes what it writes and returns the normalized record, so the caller has to be told that
        // one. Reporting the mutation's own output instead described a record that is not what is on disk.
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        store.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult(call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(new StoredNodeSettings()) with
             {
                 ToolCapableModels = ["normalized-by-the-store"]
             }));
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ToolCapableModels = ["  not-yet-normalized  "]
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal(expected: 1, result.Settings.ToolCapableModels!.Count);
        AssertEx.Equal("normalized-by-the-store", result.Settings.ToolCapableModels[0]);
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
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.ToolRelevanceEnabled)));
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.ToolApprovalPolicy)));
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.OllamaEndpoint)));
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelIsExternal_IsRejected()
    {
        // The fast model may be moved a turn's whole context onto, and that context was admitted upstream against a
        // node-local model. An external server is a process this node does not own, so the setting is refused before
        // it can ever be stored — the same pair the dispatcher re-checks per turn.
        var store = NewSubstituteStore(new StoredNodeSettings());
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
        var store = NewSubstituteStore(new StoredNodeSettings());
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
        var store = NewSubstituteStore(new StoredNodeSettings());
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
        var store = NewSubstituteStore(new StoredNodeSettings());
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
        var store = NewSubstituteStore(new StoredNodeSettings());
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
        var store = NewSubstituteStore(new StoredNodeSettings
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
        var store = NewSubstituteStore(new StoredNodeSettings
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
        var stored = new StoredNodeSettings
        {
            AutoEffortFastModelName = "qwen3-1.7b"
        };
        var store = NewSubstituteStore(stored);
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult("external"));
        var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.SaveTrustedMergedAsync(_ => stored with
        {
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("qwen3-1.7b", result.Settings.AutoEffortFastModelName);
        await store.Received(1).UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SaveTrustedMerged_WhenTheIncomingRecordHasNoMachineKey_PreservesTheStoredOne()
    {
        // The wire DTO cannot carry MachineKey, so the endpoint's merged record always arrives without one. Saving it
        // verbatim orphaned every frozen inference profile after the next restart minted a fresh key.
        var store = NewSubstituteStore(new StoredNodeSettings
        {
            MachineKey = "abc"
        });
        var service = CreateService(store);

        var result = await service.SaveTrustedMergedAsync(_ => new StoredNodeSettings
        {
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("abc", result.Settings.MachineKey);
        await store.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate, new StoredNodeSettings
                {
                    MachineKey = "abc"
                }).MachineKey == "abc"),
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
        var store = new FakeNodeSettingsStore(new StoredNodeSettings(),
            siblingWriteBeforeTheUpdate: latest => latest with
            {
                MachineKey = "minted-while-the-save-was-validating"
            });
        var service = CreateService(store);

        var result = await service.SaveTrustedMergedAsync(_ => new StoredNodeSettings
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
        var store = NewSubstituteStore(new StoredNodeSettings());
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult("external"));
        var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.SaveTrustedMergedAsync(_ => new StoredNodeSettings
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
        var store = NewSubstituteStore(new StoredNodeSettings());
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
        var store = NewSubstituteStore(new StoredNodeSettings());
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
        var store = NewSubstituteStore(new StoredNodeSettings());
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
        var store = NewSubstituteStore(new StoredNodeSettings
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
    ///     A substitute store holding <paramref name="current" />, wired to honour
    ///     <see cref="INodeSettingsStore.UpdateAsync" />'s contract: it runs the mutation against the record it holds
    ///     and RETURNS what it persisted, which is the record the service now hands back to its caller. NSubstitute's
    ///     own auto-value for that call is a null record, which no real store may return.
    /// </summary>
    private static INodeSettingsStore NewSubstituteStore(StoredNodeSettings current)
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(current);
        store.UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult(call.Arg<Func<StoredNodeSettings, StoredNodeSettings>>()(current)));
        return store;
    }
}
