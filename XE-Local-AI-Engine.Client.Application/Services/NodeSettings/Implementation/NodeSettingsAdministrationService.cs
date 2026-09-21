namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal sealed class NodeSettingsAdministrationService : INodeSettingsAdministrationService
{
    private const string AutoEffortFastModelNotLocalMessage =
        "The fast model for automatic reasoning effort must be an installed node-local model.";

    /// <summary>
    ///     How many times a save re-validates against a record that changed under it before it gives up and refuses.
    ///     A ceiling is needed at all because the alternative is a save that spins for as long as any other writer
    ///     keeps touching this file.
    /// </summary>
    private const int MaxSaveAttempts = 3;

    // Comparison-only serializer for the write-time conflict check below. Its settings are irrelevant as long as both
    // sides use the same instance; it is deliberately NOT the store's (private) one.
    private static readonly JsonSerializerOptions ComparisonSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly DefaultModelSelectionPolicy _defaultModelSelectionPolicy;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ILocalModelProviderResolver _localModelProviderResolver;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly ILogger<NodeSettingsAdministrationService> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly INodeSettingsStore _store;

    public NodeSettingsAdministrationService(
        INodeSettingsStore store,
        INodeRuntimeSettings runtimeSettings,
        DefaultModelSelectionPolicy defaultModelSelectionPolicy,
        IGgufModelStore ggufModelStore,
        IModelTrustResolver modelTrustResolver,
        ILocalModelProviderResolver localModelProviderResolver,
        ILogger<NodeSettingsAdministrationService> logger)
    {
        ArgumentNullException.ThrowIfNull(defaultModelSelectionPolicy);
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(localModelProviderResolver);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentNullException.ThrowIfNull(store);
        _defaultModelSelectionPolicy = defaultModelSelectionPolicy;
        _ggufModelStore = ggufModelStore;
        _localModelProviderResolver = localModelProviderResolver;
        _modelTrustResolver = modelTrustResolver;
        _logger = logger;
        _runtimeSettings = runtimeSettings;
        _store = store;
    }

    public async Task<StoredNodeSettings> GetTrustedSettingsAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken) ?? new StoredNodeSettings();

    public async Task<NodeSettingsAgenticView> GetAgenticViewAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetTrustedSettingsAsync(cancellationToken);
        return ToAgenticView(settings);
    }

    public async Task<NodeSettingsAdministrationResult> SaveTrustedMergedAsync(Func<StoredNodeSettings, StoredNodeSettings> merge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(merge);
        var current = await GetTrustedSettingsAsync(cancellationToken);

        // The MERGE, not a merged record, so an omitted field keeps what is stored; and the LOCAL-ONLY members ride along from that record
        // rather than from the caller — applied here, not only at the write, so a rejection path returns the key too. See ValidateAndSaveAsync.
        return await ValidateAndSaveAsync(record => merge(record) with
            {
                MachineKey = record.MachineKey,
                TranscriptionSelectedModelId = record.TranscriptionSelectedModelId,
                TranscriptionIdleTimeoutMinutes = record.TranscriptionIdleTimeoutMinutes
            },
            current,
            cancellationToken);
    }

    public async Task<NodeSettingsAdministrationResult> ApplyAgenticPatchAsync(NodeSettingsAgenticPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var current = await GetTrustedSettingsAsync(cancellationToken);
        var fieldErrors = NodeSettingsAgenticPatchValidation.Validate(patch);
        if (fieldErrors.Count > 0)
        {
            return NodeSettingsAdministrationResult.Rejected(current, fieldErrors);
        }

        if (patch.DefaultModelName is not null
            && await _defaultModelSelectionPolicy
                     .ValidateAsync(patch.DefaultModelName, LocalModelSelectionPolicy.ConfiguredModel, cancellationToken) is { } selectionFailure)
        {
            return NodeSettingsAdministrationResult.Rejected(current,
            [
                new NodeSettingsValidationError { Field = NodeSettingsField.DefaultModelName, Message = selectionFailure.DisplayMessage }
            ]);
        }

        // The PREVIOUS default model for the cache invalidation below, captured inside the projection rather than off the snapshot: the
        // transition to invalidate is the one that happened on disk, and the surviving value is the persisted run's (the projection reruns).
        var previousDefaultModelName = current.DefaultModelName;

        // The projection, not its result: a PARTIAL patch names only the fields it supplies, so every other field must come from the record
        // the write actually lands on, or a sibling writer that landed during validation is silently reverted.
        StoredNodeSettings Apply(StoredNodeSettings record)
        {
            previousDefaultModelName = record.DefaultModelName;
            return record with
            {
                DefaultModelName = TrimWhenProvided(patch.DefaultModelName, record.DefaultModelName),
                EnableTools = patch.EnableTools ?? record.EnableTools,
                ToolCapableModels = patch.ToolCapableModels ?? record.ToolCapableModels,
                HuggingFaceDefaultQuant = TrimWhenProvided(patch.HuggingFaceDefaultQuant, record.HuggingFaceDefaultQuant),
                LlamaMaxLoadedProcesses = patch.LlamaMaxLoadedProcesses ?? record.LlamaMaxLoadedProcesses,
                LlamaIdleTimeToLiveSeconds = patch.LlamaIdleTimeToLiveSeconds ?? record.LlamaIdleTimeToLiveSeconds,
                KeepModelWarmEnabled = patch.KeepModelWarmEnabled ?? record.KeepModelWarmEnabled,
                KeepModelWarmModelName = TrimWhenProvided(patch.KeepModelWarmModelName, record.KeepModelWarmModelName),
                KeepModelWarmIntervalSeconds = patch.KeepModelWarmIntervalSeconds ?? record.KeepModelWarmIntervalSeconds,
                MaxMessageRequestTimeoutSeconds = patch.MaxMessageRequestTimeoutSeconds ?? record.MaxMessageRequestTimeoutSeconds,
                ChatCacheReuse = patch.ChatCacheReuse ?? record.ChatCacheReuse,
                SpeculativeMode = TrimWhenProvided(patch.SpeculativeMode, record.SpeculativeMode),
                SpeculativeDraftModelName = TrimWhenProvided(patch.SpeculativeDraftModelName, record.SpeculativeDraftModelName),
                SpeculativeDraftMaxTokens = patch.SpeculativeDraftMaxTokens ?? record.SpeculativeDraftMaxTokens,
                SpeculativeDraftGpuLayers = patch.SpeculativeDraftGpuLayers ?? record.SpeculativeDraftGpuLayers,
                KvCacheType = TrimWhenProvided(patch.KvCacheType, record.KvCacheType),
                RerankerModelName = TrimWhenProvided(patch.RerankerModelName, record.RerankerModelName),
                AutoEffortFastModelName = TrimWhenProvided(patch.AutoEffortFastModelName, record.AutoEffortFastModelName)
            };
        }

        var result = await ValidateAndSaveAsync(Apply, current, cancellationToken);
        if (result.Updated && patch.DefaultModelName is not null)
        {
            await _defaultModelSelectionPolicy
                  .InvalidateCacheForTransitionAsync(previousDefaultModelName, result.Settings.DefaultModelName, cancellationToken);
        }

        return result;
    }

    /// <summary>
    ///     Validates the record <paramref name="apply" /> produces from the loaded snapshot, then persists the SAME
    ///     projection re-applied to the record the store holds at write time, re-validating when that record turns out
    ///     to have changed under the validation.
    /// </summary>
    /// <remarks>
    ///     Nothing is ever written that was not validated against the record it landed on: on a difference the mutation declines to project,
    ///     this method reloads and re-validates, and after <see cref="MaxSaveAttempts" /> conflicts the save is REFUSED. <paramref name="apply" />
    ///     is therefore invoked several times per save, and a caller that captures a value out of it gets the LAST invocation's value rather
    ///     than an accumulation — the run that produced the persisted record is the last one. Full protocol, and why a rebase is refused
    ///     rather than risked: docs/wiki/08-data-and-persistence.md ("`node-settings.json`: the save protocol").
    /// </remarks>
    private async Task<NodeSettingsAdministrationResult> ValidateAndSaveAsync(Func<StoredNodeSettings, StoredNodeSettings> apply,
        StoredNodeSettings current,
        CancellationToken cancellationToken)
    {
        var validatedAgainst = current;
        for (var attempt = 1; attempt <= MaxSaveAttempts; attempt++)
        {
            var settings = apply(validatedAgainst);

            // Enforcement point 1 of the node-locality gate, on BOTH save paths: a cloud id, an `ext:` id or an Ollama name here would carry
            // turn data somewhere no egress gate authorised. Only on a CHANGE — point 2 (the dispatcher) re-checks per turn. Wiki 08.
            if (!string.Equals(settings.AutoEffortFastModelName, validatedAgainst.AutoEffortFastModelName, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(settings.AutoEffortFastModelName)
                && !await IsInstalledNodeLocalModelAsync(settings.AutoEffortFastModelName, cancellationToken))
            {
                return NodeSettingsAdministrationResult.Rejected(settings,
                [
                    new NodeSettingsValidationError { Field = NodeSettingsField.AutoEffortFastModelName, Message = AutoEffortFastModelNotLocalMessage }
                ]);
            }

            var errors = await NodeSettingsPolicy.ValidateMergedAsync(settings, _runtimeSettings, cancellationToken);
            if (errors.Count > 0)
            {
                return NodeSettingsAdministrationResult.Rejected(settings, errors);
            }

            // Read-modify-write under the store's own lock, never a load here and a save there: the file is written WHOLE, so the projection
            // is re-applied to the LATEST record, MachineKey included (IMachineKeyProvider races every save on this node).
            var changedUnderTheValidation = false;
            var persisted = await _store.UpdateAsync(latest =>
                                            {
                                                changedUnderTheValidation = !SameExceptMachineKey(latest, validatedAgainst);
                                                if (changedUnderTheValidation)
                                                {
                                                    // Nothing may be projected onto a record this attempt never validated — on the last
                                                    // attempt as much as the first. Returning `latest` costs one redundant, byte-identical write.
                                                    return latest;
                                                }

                                                return apply(latest) with
                                                {
                                                    MachineKey = latest.MachineKey,
                                                    TranscriptionSelectedModelId = latest.TranscriptionSelectedModelId,
                                                    TranscriptionIdleTimeoutMinutes = latest.TranscriptionIdleTimeoutMinutes
                                                };
                                            },
                                            cancellationToken);

            if (changedUnderTheValidation)
            {
                validatedAgainst = await GetTrustedSettingsAsync(cancellationToken);
                continue;
            }

            return NodeSettingsAdministrationResult.Saved(persisted);
        }

        // The ceiling is a fixed attempt count, and reaching it refuses the save rather than serializing the writers. A version/etag on
        // INodeSettingsStore.UpdateAsync is the upgrade path if a real workload ever hits this.
        _logger.LogWarning("Node settings changed under this save on all {AttemptLimit} attempts. Nothing was written.",
            MaxSaveAttempts);
        return NodeSettingsAdministrationResult.Conflict(validatedAgainst);
    }

    /// <summary>Whether the write-time record still is the one that was validated.</summary>
    /// <remarks>
    ///     MachineKey is excluded because the projection already takes it from the write-time record, so a key minted in the window is not a
    ///     conflict to resolve. Serialize-and-compare rather than the record's own equality: <see cref="StoredNodeSettings" /> holds an
    ///     <c>IReadOnlyList&lt;string&gt;</c> and nested records, whose compiler-generated equality is by REFERENCE, so two loads of the same
    ///     stored allow-list would read as a change and burn every attempt on a difference that does not exist. The cost is two serializations
    ///     of a tiny record per save attempt; a hand-written comparer is the upgrade if it ever shows up in a profile.
    /// </remarks>
    private static bool SameExceptMachineKey(StoredNodeSettings first, StoredNodeSettings second) =>
        string.Equals(SerializeWithoutMachineKey(first), SerializeWithoutMachineKey(second), StringComparison.Ordinal);

    private static string SerializeWithoutMachineKey(StoredNodeSettings settings)
    {
        var withoutMachineKey = settings with
        {
            MachineKey = null
        };

        return JsonSerializer.Serialize(withoutMachineKey, ComparisonSerializerOptions);
    }

    // Enforcement point 1's predicate, shared verbatim with point 2 (the dispatcher's per-turn re-check) so a value this save accepts is
    // exactly a value that turn admits; the registry membership test is what stops an arbitrary string passing as "node-local llama.cpp".
    private Task<bool> IsInstalledNodeLocalModelAsync(string modelName, CancellationToken cancellationToken) =>
        NodeLocalModelGate.IsInstalledNodeLocalLlamaModelAsync(modelName,
            _ggufModelStore,
            _modelTrustResolver,
            _localModelProviderResolver,
            cancellationToken);

    private static string? TrimWhenProvided(string? value, string? current) =>
        value is null ? current : value.Trim();

    private static NodeSettingsAgenticView ToAgenticView(StoredNodeSettings settings) =>
        new()
        {
            DefaultModelName = settings.DefaultModelName,
            EnableTools = settings.EnableTools,
            ToolCapableModels = settings.ToolCapableModels,
            HuggingFaceDefaultQuant = settings.HuggingFaceDefaultQuant,
            LlamaMaxLoadedProcesses = settings.LlamaMaxLoadedProcesses,
            LlamaIdleTimeToLiveSeconds = settings.LlamaIdleTimeToLiveSeconds,
            KeepModelWarmEnabled = settings.KeepModelWarmEnabled,
            KeepModelWarmModelName = settings.KeepModelWarmModelName,
            KeepModelWarmIntervalSeconds = settings.KeepModelWarmIntervalSeconds,
            MaxMessageRequestTimeoutSeconds = settings.MaxMessageRequestTimeoutSeconds,
            ChatCacheReuse = settings.ChatCacheReuse,
            SpeculativeMode = settings.SpeculativeMode,
            SpeculativeDraftModelName = settings.SpeculativeDraftModelName,
            SpeculativeDraftMaxTokens = settings.SpeculativeDraftMaxTokens,
            SpeculativeDraftGpuLayers = settings.SpeculativeDraftGpuLayers,
            KvCacheType = settings.KvCacheType,
            RerankerModelName = settings.RerankerModelName,
            AutoEffortFastModelName = settings.AutoEffortFastModelName
        };
}
