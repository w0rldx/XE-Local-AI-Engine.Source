namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Validators;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

[Category(TestCategories.Unit)]
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
            MaxResponseSizeMb = 42,
            VoiceFeatureEnabled = true,
            ToolApprovalPolicy = new NodeToolApprovalPolicySettings()
        });
        var approvalPolicy = store.Current.ToolApprovalPolicy;
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = " new ",
            EnableTools = false,
            ChatCacheReuse = 512
        });

        AssertEx.True(result.Updated, "a valid agentic patch must be saved.");
        AssertEx.Equal(expected: 1, store.WriteCount, "an uncontended patch writes exactly once.");
        AssertEx.Equal("new", store.Current.DefaultModelName);
        AssertEx.Equal(false, store.Current.EnableTools);
        AssertEx.Equal(512, store.Current.ChatCacheReuse);
        // The excluded fields come from the write-time record, because that is where a partial patch takes every field
        // it does not name from.
        AssertEx.Equal(true, store.Current.CustomToolsEnabled);
        AssertEx.Equal(42, store.Current.MaxResponseSizeMb);
        AssertEx.Equal(true, store.Current.VoiceFeatureEnabled);
        AssertEx.True(ReferenceEquals(store.Current.ToolApprovalPolicy, approvalPolicy));
        AssertEx.Equal("new", result.Settings.DefaultModelName, "and the caller is told what was written.");
        AssertEx.Equal(false, result.Settings.EnableTools);
        AssertEx.Equal(512, result.Settings.ChatCacheReuse);
    }

    [Test]
    public async Task ApplyAgenticPatch_ModelLabWarmIdleSeconds_StoresZero_AndRefusesOutOfRangeWithTheRange()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var off = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ModelLabWarmIdleSeconds = 0
        });
        var refused = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ModelLabWarmIdleSeconds = StoredNodeSettings.MaxModelLabWarmIdleSeconds + 1
        });

        AssertEx.True(off.Updated);
        AssertEx.Equal(expected: 0, store.Current.ModelLabWarmIdleSeconds, "0 turns warming off and is stored as 0.");
        AssertEx.Equal(expected: 0, (await service.GetAgenticViewAsync()).ModelLabWarmIdleSeconds);
        AssertEx.False(refused.Updated);
        var error = refused.ValidationErrors.Single();
        AssertEx.Equal(NodeSettingsField.ModelLabWarmIdleSeconds, error.Field);
        AssertEx.Contains(error.Message, "between 0 and 3600 seconds", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, store.Current.ModelLabWarmIdleSeconds, "a refused patch writes nothing.");
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
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = 512
        });

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
        using var service = CreateService(store);

        // Request-shaped: only ChatCacheReuse is supplied, every other field comes from the record this is applied to.
        var result = await service.SaveTrustedMergedAsync(static record => new StoredNodeSettings
        {
            ChatCacheReuse = 512,
            ToolCapableModels = record.ToolCapableModels,
            DefaultModelName = record.DefaultModelName
        });

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
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            KeepModelWarmEnabled = true
        });

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
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            KeepModelWarmEnabled = true
        });

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
        using var service = CreateService(store, cloudResolver: cloudResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = "patched-model"
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("patched-model", store.Current.DefaultModelName);
        await cloudResolver.Received(1).IsCloudModelAsync("sibling-picked-model", Arg.Any<CancellationToken>());
        await cloudResolver.DidNotReceive().IsCloudModelAsync("snapshot-model", Arg.Any<CancellationToken>());
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
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ToolCapableModels = ["  not-yet-normalized  "]
        });

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
            "SpeculativeDraftGpuLayers", "KvCacheType", "RerankerModelName", "AutoEffortFastModelName", "ModelLabWarmIdleSeconds"
        ];

        AssertEx.Equal(approved.Length, names.Count);
        AssertEx.True(names.SetEquals(approved), "the agentic patch must expose exactly the approved 19 fields.");
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.CustomToolsEnabled)));
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.ToolRelevanceEnabled)));
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.ToolApprovalPolicy)));
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
        using var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "ext:studio/qwen3-1.7b"
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelIsCloud_IsRejected()
    {
        var store = NewSubstituteStore(new StoredNodeSettings());
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ModelTrustLocality.Cloud));
        using var service = CreateService(store, modelTrustResolver: trustResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "gpt-5.6-terra"
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
    }

    [Test]
    [Arguments("nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0", true)]
    [Arguments("ggml-org/embeddinggemma-2-GGUF:BF16", false)]
    [Arguments("unsloth/Qwen3-8B-GGUF:Q4_K_M", false)]
    [Arguments("CompendiumLabs/bge-m3-gguf:Q8_0", true)]
    public async Task SaveTrusted_KnowledgeEmbeddingPin_AcceptsOnlyAnInstalledEmbeddingModel(string pin, bool accepted)
    {
        // Installed: the nomic and bge-m3 embedders and a chat model. A pin naming a missing embedder or a non-embedding model is refused.
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = CreateService(store,
            localModelProviderResolver: EmbeddingProviderResolver("nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0", "CompendiumLabs/bge-m3-gguf:Q8_0", "unsloth/Qwen3-8B-GGUF:Q4_K_M"));

        var result = await service.SaveTrustedMergedAsync(current => current with
        {
            KnowledgeEmbeddingModelName = pin
        });

        AssertEx.Equal(accepted, result.Updated);
        if (accepted)
        {
            AssertEx.Equal(pin, store.Current.KnowledgeEmbeddingModelName);
        }
        else
        {
            AssertEx.Equal(NodeSettingsField.KnowledgeEmbeddingModelName, result.ValidationErrors.Single().Field);
            AssertEx.Null(store.Current.KnowledgeEmbeddingModelName);
        }
    }

    [Test]
    public async Task SaveTrusted_UnchangedPinOfAnUninstalledModel_DoesNotBlockAnUnrelatedSave()
    {
        // The pin is judged only on a change: an embedder removed after pinning must not lock the settings page.
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            KnowledgeEmbeddingModelName = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0"
        });
        using var service = CreateService(store, localModelProviderResolver: EmbeddingProviderResolver());

        var result = await service.SaveTrustedMergedAsync(current => current with
        {
            EnableTools = false
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0", store.Current.KnowledgeEmbeddingModelName);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SaveTrusted_ClearingThePin_IsRefusedOnlyWhileADocumentIsIndexed(bool corpusIndexed)
    {
        // A clear on an indexed corpus would re-arm the first-by-name fallback; on an empty corpus it stays allowed.
        const string Pin = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0";
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            KnowledgeEmbeddingModelName = Pin
        });
        using var service = CreateService(store,
            localModelProviderResolver: EmbeddingProviderResolver(Pin),
            indexedEmbeddingModels: corpusIndexed ? [Pin] : []);

        var result = await service.SaveTrustedMergedAsync(current => current with
        {
            KnowledgeEmbeddingModelName = null
        });

        AssertEx.Equal(!corpusIndexed, result.Updated);
        if (corpusIndexed)
        {
            var error = result.ValidationErrors.Single();
            AssertEx.Equal(NodeSettingsField.KnowledgeEmbeddingModelName, error.Field);
            AssertEx.Contains(error.Message, Pin);
            AssertEx.Contains(error.Message, "set an installed embedding model instead");
            AssertEx.Equal(Pin, store.Current.KnowledgeEmbeddingModelName);
        }
        else
        {
            AssertEx.Null(store.Current.KnowledgeEmbeddingModelName);
        }
    }

    [Test]
    public async Task SaveTrusted_PinOfAMissingModel_IsRefusedWithAMessageThatNullClears()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = CreateService(store, localModelProviderResolver: EmbeddingProviderResolver());

        var result = await service.SaveTrustedMergedAsync(current => current with
        {
            KnowledgeEmbeddingModelName = "ggml-org/embeddinggemma-2-GGUF:BF16"
        });

        var message = result.ValidationErrors.Single().Message;
        AssertEx.Contains(message, "null clears the pin only while no document is indexed");
        AssertEx.False(message.Contains("blank", StringComparison.Ordinal), "the message must not offer a blank value.");
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelIsNotInstalled_IsRejected()
    {
        // The hole this closes: with no cloud provider configured, `ModelTrustResolver` classifies a
        // scheme-less id as Local and `LocalModelProviderResolver` routes an unmapped id to the default provider
        // (llamacpp), so the pair alone accepted ANY string — a cloud model id saved with HTTP 200. Registry
        // membership is what refuses it.
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store, fastModelInstalled: false);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "gpt-4o-mini"
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Save_WhenAutoEffortFastModelIsNodeLocalButNotServedByLlamaCpp_IsRejected()
    {
        // Node-local is not enough. The swap targets a llama.cpp process — the only provider the capacity gate and the
        // liveness probe can reason about — so a trusted-local model on any other provider is refused at the same point.
        var store = NewSubstituteStore(new StoredNodeSettings());
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult("external"));
        using var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "qwen3:1.7b"
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Save_WhenMaxLoadedProcessesIsOne_IsRejected()
    {
        // The fast model is a SECOND chat process alongside the conversation's own, so a node capped at one slot could
        // never admit it: the setting would look configured and silently never apply.
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            AutoEffortFastModelName = "qwen3-1.7b",
            LlamaMaxLoadedProcesses = 1
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.LlamaMaxLoadedProcesses, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The MCP admin patch applies the same shape rules as the REST save, with the same message, so an unknown quant
    ///     is refused instead of stored and the caller is told which field.
    /// </summary>
    [Test]
    [Arguments("Q9_BOGUS", null, NodeSettingsField.HuggingFaceDefaultQuant)]
    [Arguments("Q4_K_M-extra", null, NodeSettingsField.HuggingFaceDefaultQuant)]
    [Arguments(null, "unsloth/model\u0001-GGUF:Q4_K_M", NodeSettingsField.DefaultModelName)]
    public async Task ApplyAgenticPatch_WithAnUnknownQuantOrAMalformedDefaultModel_IsRejectedLikeTheRestSave(string? quant,
        string? modelName,
        NodeSettingsField field)
    {
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            HuggingFaceDefaultQuant = quant,
            DefaultModelName = modelName
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(field, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());

        var rest = await new SaveNodeSettingsRequestValidator().ValidateAsync(new SaveNodeSettingsRequest
        {
            HuggingFaceDefaultQuant = quant,
            DefaultModelName = modelName
        });
        AssertEx.Equal(1, rest.Errors.Count, "the REST save refuses the same value");
        AssertEx.Equal(result.ValidationErrors[0].Message, rest.Errors[0].ErrorMessage, "both paths refuse with one message");
    }

    [Test]
    public async Task ApplyAgenticPatch_WithAKnownQuantInOddCasing_IsSaved()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            HuggingFaceDefaultQuant = "ud-q4_k_xl"
        });

        AssertEx.True(result.Updated, "a canonical quant token, case aside, is a valid default");
        AssertEx.Equal("ud-q4_k_xl", store.Current.HuggingFaceDefaultQuant);
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
        using var service = CreateService(store, modelTrustResolver: trustResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = 512
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("qwen3-1.7b", result.Settings.AutoEffortFastModelName);
        await trustResolver.DidNotReceiveWithAnyArgs().ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
        using var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = 512
        });

        AssertEx.True(result.Updated, "an unrelated patch must not be rejected over a stored fast model.");
        AssertEx.Equal("qwen3-1.7b", result.Settings.AutoEffortFastModelName);
        await store.Received(1).UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
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
        using var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.SaveTrustedMergedAsync(_ => stored with
        {
            ChatCacheReuse = 512
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("qwen3-1.7b", result.Settings.AutoEffortFastModelName);
        await store.Received(1).UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
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
        using var service = CreateService(store);

        var result = await service.SaveTrustedMergedAsync(_ => new StoredNodeSettings
        {
            ChatCacheReuse = 512
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("abc", result.Settings.MachineKey);
        await store.Received(1).UpdateAsync(Arg.Is<Func<StoredNodeSettings, StoredNodeSettings>>(mutate =>
                Persisted(mutate, new StoredNodeSettings
                {
                    MachineKey = "abc"
                }).MachineKey == "abc"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveTrustedMerged_KeepsTheLocalOnlyModelChoice_AndHonoursTheWireIdleTimeout()
    {
        // The model choice is LOCAL-ONLY: the wire DTO cannot carry it, so the endpoint's merged record always arrives
        // without it and saving that verbatim would reset it on every unrelated save. The idle timeout is on the wire now,
        // so the endpoint's merge owns it and the service must NOT overwrite it with the stored value.
        var store = NewSubstituteStore(new StoredNodeSettings
        {
            TranscriptionSelectedModelId = "large-v3-turbo",
            TranscriptionIdleTimeoutMinutes = 42
        });
        using var service = CreateService(store);

        var result = await service.SaveTrustedMergedAsync(_ => new StoredNodeSettings
        {
            ChatCacheReuse = 512,
            TranscriptionIdleTimeoutMinutes = 30
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("large-v3-turbo", AssertEx.NotNull(result.Settings.TranscriptionSelectedModelId));
        AssertEx.Equal(expected: 30, result.Settings.TranscriptionIdleTimeoutMinutes);
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
        using var service = CreateService(store);

        var result = await service.SaveTrustedMergedAsync(_ => new StoredNodeSettings
        {
            ChatCacheReuse = 512
        });

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
        using var service = CreateService(store, localModelProviderResolver: providerResolver);

        var result = await service.SaveTrustedMergedAsync(_ => new StoredNodeSettings
        {
            AutoEffortFastModelName = "ext:studio/qwen3-1.7b"
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.AutoEffortFastModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenMergedPolicyRejects_DoesNotSave()
    {
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            KeepModelWarmEnabled = true,
            KeepModelWarmModelName = " "
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.KeepModelWarmModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(NodeSettingsField.KeepModelWarmModelName)]
    [Arguments(NodeSettingsField.RerankerModelName)]
    [Arguments(NodeSettingsField.AutoEffortFastModelName)]
    public async Task ApplyAgenticPatchAsync_WhenARoleModelNameIsMalformed_RejectsItWithoutSaving(NodeSettingsField field)
    {
        const string malformed = "unsloth/model\u0001-GGUF:Q4_K_M";
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(field switch
        {
            NodeSettingsField.KeepModelWarmModelName => new NodeSettingsAgenticPatch
            {
                KeepModelWarmModelName = malformed
            },
            NodeSettingsField.RerankerModelName => new NodeSettingsAgenticPatch
            {
                RerankerModelName = malformed
            },
            _ => new NodeSettingsAgenticPatch
            {
                AutoEffortFastModelName = malformed
            }
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(field, result.ValidationErrors[0].Field);
        AssertEx.Equal(StoredNodeSettings.ImplausibleModelNameMessage, result.ValidationErrors[0].Message);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenFieldRangeIsInvalid_DoesNotNormalizeSaveOrReport()
    {
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = StoredNodeSettings.MaxChatCacheReuse + 1
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(NodeSettingsField.ChatCacheReuse, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenToolCapableModelsIsEmpty_DoesNotSave()
    {
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ToolCapableModels = []
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(NodeSettingsField.ToolCapableModels, result.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
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
        using var service = CreateService(store, cloudResolver: cloudResolver, cloudFactory: cloudFactory);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = "local-model"
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("local-model", result.Settings.DefaultModelName);
        cloudFactory.Received(1).InvalidateSelectionCache();
    }

    private static ILocalModelProviderResolver EmbeddingProviderResolver(params string[] installedModels)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(installedModels
                                                                              .Select(static name => new LocalModelDescriptor
                                                                              {
                                                                                  ModelName = name,
                                                                                  ProviderName = LlamaServerProviderConstants.ProviderName,
                                                                                  IsAvailable = true,
                                                                                  SizeBytes = null,
                                                                                  ModifiedAt = null,
                                                                                  MaxContextTokens = null,
                                                                                  Capabilities = []
                                                                              })
                                                                              .ToList()));
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProvider(Arg.Any<string>()).Returns(provider);
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        return resolver;
    }

    [Test]
    [Arguments("bge-reranker-v2-m3", 1)]
    [Arguments("old-reranker", 0)]
    [Arguments("", 0)]
    public async Task ApplyAgenticPatchAsync_WarmsTheRerankerOnlyWhenASavedNameChanges(string patchedName, int expectedWarms)
    {
        // The agentic (MCP) path and the REST path share ValidateAndSaveAsync, so this pins both: a new name warms once,
        // an unchanged one does not, and a cleared one falls back to the configured seed the startup warm covered.
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            RerankerModelName = "old-reranker"
        });
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();
        using var service = CreateService(store, knowledgeModelPrewarmer: prewarmer);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            RerankerModelName = patchedName
        });

        AssertEx.True(result.Updated, "a well-formed reranker patch must be saved.");
        prewarmer.Received(expectedWarms).RequestWarm();
    }

    [Test]
    public async Task SaveTrustedMergedAsync_WhenTheRerankerNameChanges_RequestsOneWarm()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();
        using var service = CreateService(store, knowledgeModelPrewarmer: prewarmer);

        var result = await service.SaveTrustedMergedAsync(record => record with
        {
            RerankerModelName = "bge-reranker-v2-m3"
        });

        AssertEx.True(result.Updated);
        prewarmer.Received(1).RequestWarm();
    }

    [Test]
    public async Task SaveTrustedMergedAsync_WhenTheSaveIsRejected_RequestsNoWarm()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings());
        var prewarmer = Substitute.For<IKnowledgeModelPrewarmer>();
        using var service = CreateService(store, knowledgeModelPrewarmer: prewarmer);

        var result = await service.SaveTrustedMergedAsync(record => record with
        {
            RerankerModelName = "bge-reranker-v2-m3",
            // Keep-warm on with no model is refused by the merged-record policy, so nothing is written.
            KeepModelWarmEnabled = true
        });

        AssertEx.False(result.Updated);
        prewarmer.DidNotReceive().RequestWarm();
    }

    /// <summary>
    ///     The save applies the COMMITTED record: the real store resets an out-of-range cap of 99 to unset, so proposal and commit differ.
    ///     A cleared field falls back to the seed (image) or the default (llama).
    /// </summary>
    [Test]
    public async Task Save_AppliesTheCommittedCapsAndTtls_AndAClearAppliesTheSeed()
    {
        using var dataDir = new TempDirectory("xe-settings-apply");
        using var store = new NodeSettingsStore(new FakeNodeDataDirectory(dataDir.Path), NullLogger<NodeSettingsStore>.Instance);
        // The running node on a cap of 7, so neither "unchanged" nor "the proposal" can pass for the committed default.
        var llama = new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = 7
        };
        var image = new StableDiffusionRuntimeOptions();
        using var service = CreateService(store,
            llamaOptions: llama,
            imageOptions: image,
            runtimeSeed: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["StableDiffusionRuntime:MaxLoadedProcesses"] = "3",
                ["StableDiffusionRuntime:IdleTimeToLive"] = "00:20:00"
            });

        var saved = await service.SaveTrustedMergedAsync(record => record with
        {
            LlamaMaxLoadedProcesses = 99,
            LlamaIdleTimeToLiveSeconds = 600,
            ImageMaxLoadedProcesses = 4,
            ImageIdleTimeToLiveSeconds = 120
        });

        AssertEx.True(saved.Updated);
        AssertEx.Null(saved.Settings.LlamaMaxLoadedProcesses);
        AssertEx.Equal(StoredNodeSettings.DefaultLlamaMaxLoadedProcesses, llama.MaxLoadedProcesses);
        AssertEx.Equal(TimeSpan.FromSeconds(600), llama.IdleTimeToLive);
        AssertEx.Equal(4, image.MaxLoadedProcesses);
        AssertEx.Equal(TimeSpan.FromSeconds(120), image.IdleTimeToLive);

        var cleared = await service.SaveTrustedMergedAsync(record => record with
        {
            LlamaMaxLoadedProcesses = 5,
            LlamaIdleTimeToLiveSeconds = null,
            ImageMaxLoadedProcesses = null,
            ImageIdleTimeToLiveSeconds = null
        });

        AssertEx.True(cleared.Updated);
        AssertEx.Equal(5, llama.MaxLoadedProcesses);
        AssertEx.Equal(TimeSpan.FromSeconds(StoredNodeSettings.DefaultLlamaIdleTimeToLiveSeconds), llama.IdleTimeToLive);
        AssertEx.Equal(3, image.MaxLoadedProcesses);
        AssertEx.Equal(TimeSpan.FromMinutes(20), image.IdleTimeToLive);
    }

    /// <summary>A save that touches none of the four leaves the running options as they are, never on a default the sync loader answers.</summary>
    [Test]
    public async Task Save_AnUnrelatedSave_LeavesTheRunningLimits()
    {
        var store = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            LlamaMaxLoadedProcesses = 5,
            LlamaIdleTimeToLiveSeconds = 600,
            ImageMaxLoadedProcesses = 2,
            ImageIdleTimeToLiveSeconds = 120
        });
        var llama = new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = 5,
            IdleTimeToLive = TimeSpan.FromSeconds(600)
        };
        var image = new StableDiffusionRuntimeOptions
        {
            MaxLoadedProcesses = 2,
            IdleTimeToLive = TimeSpan.FromSeconds(120)
        };
        using var service = CreateService(store, llamaOptions: llama, imageOptions: image);

        var result = await service.SaveTrustedMergedAsync(record => record with
        {
            ChatCacheReuse = 512
        });

        AssertEx.True(result.Updated);
        AssertLimits(llama, image, 5, TimeSpan.FromSeconds(600), 2, TimeSpan.FromSeconds(120));
    }

    /// <summary>Neither a policy rejection nor a save that loses every attempt to a concurrent writer touches the running limits.</summary>
    [Test]
    public async Task Save_ARejectedSave_LeavesTheRunningLimits()
    {
        var llama = new LlamaServerSupervisorOptions();
        var image = new StableDiffusionRuntimeOptions();
        var rejectingStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var rejecting = CreateService(rejectingStore, llamaOptions: llama, imageOptions: image);

        var rejected = await rejecting.SaveTrustedMergedAsync(record => record with
        {
            LlamaMaxLoadedProcesses = 5,
            ImageMaxLoadedProcesses = 4,
            // Keep-warm on with no model is refused by the merged-record policy.
            KeepModelWarmEnabled = true
        });

        AssertEx.False(rejected.Updated);
        AssertLimits(llama, image, 3, TimeSpan.FromMinutes(15), 1, TimeSpan.FromMinutes(15));

        // The sibling rewrites the record on every attempt, so the save never lands; what the sibling writes is not this save's to apply.
        var siblingWrites = 0;
        var conflictingStore = new FakeNodeSettingsStore(new StoredNodeSettings(),
            siblingWriteBeforeTheUpdate: latest => latest with
            {
                ImageMaxLoadedProcesses = 4,
                ToolCapableModels = [$"sibling-{++siblingWrites}"]
            });
        using var conflicting = CreateService(conflictingStore, llamaOptions: llama, imageOptions: image);

        var conflicted = await conflicting.SaveTrustedMergedAsync(record => record with
        {
            LlamaMaxLoadedProcesses = 5
        });

        AssertEx.True(conflicted.Conflicted);
        AssertLimits(llama, image, 3, TimeSpan.FromMinutes(15), 1, TimeSpan.FromMinutes(15));
    }

    /// <summary>A caller that gives up after the commit (a disconnected client) still leaves the node on the committed limits.</summary>
    [Test]
    public async Task Save_CancelledAfterCommit_StillApplies()
    {
        using var caller = new CancellationTokenSource();
        var store = new CancelAfterCommitStore(new FakeNodeSettingsStore(new StoredNodeSettings()), caller);
        var llama = new LlamaServerSupervisorOptions();
        var image = new StableDiffusionRuntimeOptions();
        using var service = CreateService(store, llamaOptions: llama, imageOptions: image);

        var result = await service.SaveTrustedMergedAsync(record => record with
            {
                LlamaMaxLoadedProcesses = 5,
                LlamaIdleTimeToLiveSeconds = 600,
                ImageMaxLoadedProcesses = 2,
                ImageIdleTimeToLiveSeconds = 120
            },
            caller.Token);

        AssertEx.True(caller.IsCancellationRequested);
        AssertEx.True(result.Updated);
        AssertLimits(llama, image, 5, TimeSpan.FromSeconds(600), 2, TimeSpan.FromSeconds(120));
    }

    /// <summary>
    ///     Two overlapping saves apply in commit order: the first commits and stalls before its apply, the second is started meanwhile,
    ///     and the running options end on the record the store holds, not on the older save's limits.
    /// </summary>
    /// <remarks>
    ///     Every await before the store call completes synchronously here, so the second save has reached the commit gate (or, without
    ///     it, finished) when its call returns. No sleep.
    /// </remarks>
    [Test]
    public async Task Save_TwoOverlappingSaves_ApplyTheLastCommittedRecord()
    {
        var store = new PauseFirstCommitStore(new FakeNodeSettingsStore(new StoredNodeSettings()));
        var llama = new LlamaServerSupervisorOptions();
        var image = new StableDiffusionRuntimeOptions();
        using var service = CreateService(store, llamaOptions: llama, imageOptions: image);

        var first = service.SaveTrustedMergedAsync(static record => record with
        {
            LlamaMaxLoadedProcesses = 5,
            LlamaIdleTimeToLiveSeconds = 600
        });
        await store.FirstCommitted.WaitAsync(TestBudgets.Contended);
        var second = service.SaveTrustedMergedAsync(static record => record with
        {
            LlamaMaxLoadedProcesses = 6,
            ImageMaxLoadedProcesses = 2
        });
        store.ReleaseFirst();
        var results = await Task.WhenAll(first, second).WaitAsync(TestBudgets.Contended);

        AssertEx.True(results.All(static result => result.Updated));
        var committed = store.Current;
        AssertEx.Equal(6, committed.LlamaMaxLoadedProcesses);
        AssertEx.Equal(2, committed.ImageMaxLoadedProcesses);
        AssertLimits(llama, image, 6, TimeSpan.FromSeconds(600), 2, TimeSpan.FromMinutes(15));
    }

    private static void AssertLimits(LlamaServerSupervisorOptions llama,
        StableDiffusionRuntimeOptions image,
        int llamaCap,
        TimeSpan llamaTtl,
        int imageCap,
        TimeSpan imageTtl)
    {
        AssertEx.Equal(llamaCap, llama.MaxLoadedProcesses);
        AssertEx.Equal(llamaTtl, llama.IdleTimeToLive);
        AssertEx.Equal(imageCap, image.MaxLoadedProcesses);
        AssertEx.Equal(imageTtl, image.IdleTimeToLive);
    }

    /// <summary>Commits the FIRST update through the fake and holds its return until <see cref="ReleaseFirst" />; later updates pass straight through.</summary>
    private sealed class PauseFirstCommitStore : INodeSettingsStore
    {
        private readonly TaskCompletionSource _firstCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FakeNodeSettingsStore _inner;
        private int _updates;

        public PauseFirstCommitStore(FakeNodeSettingsStore inner)
        {
            _inner = inner;
        }

        public Task FirstCommitted => _firstCommitted.Task;

        public StoredNodeSettings Current => _inner.Current;

        public void ReleaseFirst() =>
            _release.TrySetResult();

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(cancellationToken);

        public StoredNodeSettings Load(CancellationToken cancellationToken = default) =>
            _inner.Load(cancellationToken);

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default) =>
            _inner.SaveAsync(settings, cancellationToken);

        public async Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
        {
            var committed = await _inner.UpdateAsync(mutate, cancellationToken);
            if (Interlocked.Increment(ref _updates) == 1)
            {
                _firstCommitted.TrySetResult();
                await _release.Task.WaitAsync(TestBudgets.Contended, cancellationToken);
            }

            return committed;
        }
    }

    /// <summary>Commits through the fake, then cancels the caller's token before returning: the disconnect lands after the commit.</summary>
    private sealed class CancelAfterCommitStore : INodeSettingsStore
    {
        private readonly CancellationTokenSource _caller;
        private readonly FakeNodeSettingsStore _inner;

        public CancelAfterCommitStore(FakeNodeSettingsStore inner, CancellationTokenSource caller)
        {
            _inner = inner;
            _caller = caller;
        }

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(cancellationToken);

        public StoredNodeSettings Load(CancellationToken cancellationToken = default) =>
            _inner.Load(cancellationToken);

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default) =>
            _inner.SaveAsync(settings, cancellationToken);

        public async Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
        {
            var committed = await _inner.UpdateAsync(mutate, cancellationToken);
            await _caller.CancelAsync();
            return committed;
        }
    }

    private static NodeSettingsAdministrationService CreateService(INodeSettingsStore store,
        ICloudModelResolver? cloudResolver = null,
        IActiveCloudChatClientFactory? cloudFactory = null,
        INodeRuntimeSettings? runtimeSettings = null,
        IModelTrustResolver? modelTrustResolver = null,
        ILocalModelProviderResolver? localModelProviderResolver = null,
        bool fastModelInstalled = true,
        IKnowledgeModelPrewarmer? knowledgeModelPrewarmer = null,
        LlamaServerSupervisorOptions? llamaOptions = null,
        StableDiffusionRuntimeOptions? imageOptions = null,
        IDictionary<string, string?>? runtimeSeed = null,
        params string[] indexedEmbeddingModels)
    {
        var runtime = runtimeSettings ?? Substitute.For<INodeRuntimeSettings>();

        // The effective values come from the real resolution (stored > configuration seed > default) over the record passed in.
        var resolution = SeededNodeRuntimeSettings.Create(runtimeSeed);
        runtime.ResolveEffectiveValues(Arg.Any<StoredNodeSettings>()).Returns(call => resolution.ResolveEffectiveValues(call.Arg<StoredNodeSettings>()));

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

        cloudResolver ??= Substitute.For<ICloudModelResolver>();
        cloudFactory ??= Substitute.For<IActiveCloudChatClientFactory>();

        // Registry membership is the third point of the fast-model gate, and the only one that refuses an id neither
        // resolver has ever heard of: both of them default an unknown name to node-local llama.cpp.
        var ggufModelStore = Substitute.For<IGgufModelStore>();
        ggufModelStore.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(fastModelInstalled));

        var selectionPolicy = new DefaultModelSelectionPolicy(ggufModelStore,
            cloudResolver,
            cloudFactory,
            new ModelNameValidator(Options.Create(new SecurityOptions())),
            new ServiceCollection().AddSingleton(Substitute.For<IModelClassificationStore>())
                                   .BuildServiceProvider()
                                   .GetRequiredService<IServiceScopeFactory>());
        return new NodeSettingsAdministrationService(store,
            runtime,
            selectionPolicy,
            ggufModelStore,
            modelTrustResolver,
            localModelProviderResolver,
            Options.Create(new KnowledgeBaseOptions()),
            knowledgeModelPrewarmer ?? Substitute.For<IKnowledgeModelPrewarmer>(),
            CatalogScopeFactory(indexedEmbeddingModels),
            llamaOptions ?? new LlamaServerSupervisorOptions(),
            imageOptions ?? new StableDiffusionRuntimeOptions(),
            NullLogger<NodeSettingsAdministrationService>.Instance);
    }

    // The distinct embedding models of the Indexed documents; none is an empty corpus.
    private static IServiceScopeFactory CatalogScopeFactory(IReadOnlyList<string> indexedEmbeddingModels)
    {
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.ListIndexedEmbeddingModelsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(call => Task.FromResult<IReadOnlyList<string>>(indexedEmbeddingModels.Take(call.Arg<int>()).ToList()));
        return new ServiceCollection().AddScoped(_ => catalog)
                                      .BuildServiceProvider()
                                      .GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    ///     The record a save actually persists. The service writes through <see cref="INodeSettingsStore.UpdateAsync" />,
    ///     so what lands is its mutation applied to the settings the store holds AT WRITE TIME — which is not
    ///     necessarily what the service loaded.
    /// </summary>
    private static StoredNodeSettings Persisted(Func<StoredNodeSettings, StoredNodeSettings> mutate, StoredNodeSettings? latest = null) =>
        mutate(latest ?? new StoredNodeSettings());

    [Test]
    public async Task Save_WhenABackgroundModelIsAnInstalledGguf_IsAccepted()
    {
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store);

        var result = await service.SaveTrustedMergedAsync(static record => record with
        {
            PlaybookAnalysisModelName = "bartowski/Qwen2.5-3B-Instruct-GGUF:Q4_K_M"
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("bartowski/Qwen2.5-3B-Instruct-GGUF:Q4_K_M", result.Settings.PlaybookAnalysisModelName);
    }

    [Test]
    [Arguments("gpt-4o-mini")]
    [Arguments("qwen3:8b")]
    public async Task Save_WhenABackgroundModelIsNotAnInstalledGguf_IsRejectedOnItsFieldNamingTheRequirement(string modelName)
    {
        // Both resolvers call an unknown id local, so GGUF registry membership is what refuses a typo, a cloud id saved by
        // name, or a tag-style name an older node could serve from another runtime.
        var store = NewSubstituteStore(new StoredNodeSettings());
        using var service = CreateService(store, fastModelInstalled: false);

        var result = await service.SaveTrustedMergedAsync(record => record with
        {
            PlaybookEvalModelName = modelName
        });

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.PlaybookEvalModelName, result.ValidationErrors[0].Field);
        AssertEx.Equal("The model must be an installed node-local GGUF model, or blank to use the default model.", result.ValidationErrors[0].Message);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Save_WhenABackgroundModelIsCloudOrExternal_IsRejected()
    {
        var store = NewSubstituteStore(new StoredNodeSettings());
        var trustResolver = Substitute.For<IModelTrustResolver>();
        trustResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ModelTrustLocality.Cloud));
        using var cloudService = CreateService(store, modelTrustResolver: trustResolver);

        var cloud = await cloudService.SaveTrustedMergedAsync(static record => record with
        {
            MemoryExtractionModelName = "gpt-5.6-terra"
        });

        // An `ext:` id is refused by its scheme even when its connection is declared Local and the registry says installed.
        using var externalService = CreateService(store);
        var external = await externalService.SaveTrustedMergedAsync(static record => record with
        {
            PlaybookAnalysisModelName = "ext:lab-box/llama-70b"
        });

        AssertEx.Equal(NodeSettingsField.MemoryExtractionModelName, cloud.ValidationErrors[0].Field);
        AssertEx.Equal(NodeSettingsField.PlaybookAnalysisModelName, external.ValidationErrors[0].Field);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Save_WhenABackgroundModelIsUnchangedOrBlank_IsNotJudged()
    {
        // A stored model that was later uninstalled must not block an unrelated save, and blank means inherit.
        var store = NewSubstituteStore(new StoredNodeSettings
        {
            PlaybookEvalModelName = "since-uninstalled:7b"
        });
        using var service = CreateService(store, fastModelInstalled: false);

        var result = await service.SaveTrustedMergedAsync(static record => record with
        {
            MemoryExtractionModelName = null,
            ChatRetentionDays = 7
        });

        AssertEx.True(result.Updated);
        AssertEx.Equal("since-uninstalled:7b", result.Settings.PlaybookEvalModelName);
    }

    /// <summary>
    ///     A substitute store holding <paramref name="current" />, wired to honor
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
