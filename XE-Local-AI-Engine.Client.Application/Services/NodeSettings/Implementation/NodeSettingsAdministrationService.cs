namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal sealed class NodeSettingsAdministrationService(
    INodeSettingsStore store,
    INodeRuntimeSettings runtimeSettings,
    ICapabilityReporter capabilityReporter,
    DefaultModelSelectionPolicy defaultModelSelectionPolicy,
    IGgufModelStore ggufModelStore,
    IModelTrustResolver modelTrustResolver,
    ILocalModelProviderResolver localModelProviderResolver,
    ILogger<NodeSettingsAdministrationService> logger) : INodeSettingsAdministrationService
{
    private const string AutoEffortFastModelNotLocalMessage =
        "The fast model for automatic reasoning effort must be an installed node-local model.";

    private readonly ICapabilityReporter _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
    private readonly DefaultModelSelectionPolicy _defaultModelSelectionPolicy = defaultModelSelectionPolicy ?? throw new ArgumentNullException(nameof(defaultModelSelectionPolicy));
    private readonly IGgufModelStore _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
    private readonly ILocalModelProviderResolver _localModelProviderResolver = localModelProviderResolver ?? throw new ArgumentNullException(nameof(localModelProviderResolver));
    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
    private readonly ILogger<NodeSettingsAdministrationService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INodeRuntimeSettings _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    private readonly INodeSettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<StoredNodeSettings> GetTrustedSettingsAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new StoredNodeSettings();

    public async Task<NodeSettingsAgenticView> GetAgenticViewAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetTrustedSettingsAsync(cancellationToken).ConfigureAwait(false);
        return ToAgenticView(settings);
    }

    public async Task<NodeSettingsAdministrationResult> SaveTrustedMergedAsync(StoredNodeSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = await GetTrustedSettingsAsync(cancellationToken).ConfigureAwait(false);

        // LOCAL-ONLY members ride along from the stored record instead of from the caller. MachineKey is minted
        // node-side by IMachineKeyProvider and is deliberately absent from the wire DTO, so a caller that builds a
        // StoredNodeSettings out of a request has no value to supply and saving its record verbatim would erase the
        // key. That is silent data loss: the next start mints a fresh key, and every frozen inference profile — keyed
        // by machine key — is orphaned while still reading as frozen.
        //
        // Kept beside the persistence-boundary guard in ValidateAndSaveAsync rather than replaced by it: that one
        // owns the key that is finally WRITTEN, this one owns the record this call VALIDATES and returns, including
        // on the rejection paths that never reach a write.
        var merged = settings with
        {
            MachineKey = current.MachineKey
        };

        // Whole-record by design, so the projection deliberately IGNORES the write-time record: the wire DTO carries
        // every field, and the endpoint's contract is "this record replaces the stored one". Only MachineKey is
        // refreshed from the latest record, by the shared write below.
        return await ValidateAndSaveAsync(_ => merged, current, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeSettingsAdministrationResult> ApplyAgenticPatchAsync(NodeSettingsAgenticPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var current = await GetTrustedSettingsAsync(cancellationToken).ConfigureAwait(false);
        var fieldErrors = NodeSettingsAgenticPatchValidation.Validate(patch);
        if (fieldErrors.Count > 0)
        {
            return NodeSettingsAdministrationResult.Rejected(current, fieldErrors);
        }

        if (patch.DefaultModelName is not null
            && await _defaultModelSelectionPolicy
                     .ValidateAsync(patch.DefaultModelName, LocalModelSelectionPolicy.ConfiguredModel, cancellationToken)
                     .ConfigureAwait(false) is { } selectionFailure)
        {
            return NodeSettingsAdministrationResult.Rejected(current,
            [
                new NodeSettingsValidationError(NodeSettingsField.DefaultModelName, selectionFailure.DisplayMessage)
            ]);
        }

        // The projection, not its result: a PARTIAL patch names only the fields it supplies, so every other field must
        // come from the record the write actually lands on. Applying this to the snapshot loaded above and saving THAT
        // whole record wrote back the snapshot's value for every unnamed field, silently reverting any sibling writer
        // — a tool-capable-model registration, a default-model selection — that landed while this call validated.
        StoredNodeSettings Apply(StoredNodeSettings record) => record with
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

        var result = await ValidateAndSaveAsync(Apply, current, cancellationToken).ConfigureAwait(false);
        if (result.Updated && patch.DefaultModelName is not null)
        {
            await _defaultModelSelectionPolicy
                  .InvalidateCacheForTransitionAsync(current.DefaultModelName, result.Settings.DefaultModelName, cancellationToken)
                  .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    ///     Validates the record <paramref name="apply" /> produces from the loaded snapshot, then persists the SAME
    ///     projection re-applied to the record the store holds at write time.
    /// </summary>
    /// <remarks>
    ///     Validation necessarily runs against the snapshot: the policy checks are async (they resolve models) and the
    ///     store's mutation must stay pure and synchronous under its lock. The write therefore re-applies
    ///     <paramref name="apply" /> rather than saving the validated preview, so fields the caller never supplied come
    ///     from the write-time record instead of a stale copy. There is deliberately no compare-and-retry: a sibling
    ///     writer that changes a validated field between the two is the same window every other whole-file writer on
    ///     this node has, and the dispatcher re-checks the one security-relevant value per turn.
    /// </remarks>
    private async Task<NodeSettingsAdministrationResult> ValidateAndSaveAsync(Func<StoredNodeSettings, StoredNodeSettings> apply,
        StoredNodeSettings current,
        CancellationToken cancellationToken)
    {
        var settings = apply(current);

        // Enforcement point 1 of the node-locality gate, on BOTH save paths (the endpoint's merged save and the MCP
        // patch) rather than only the patch: the runner's dispatcher may move an `auto` turn onto this model, and the
        // turn's data was admitted upstream against a node-local one. A cloud id, an `ext:` id or an Ollama name would
        // carry that data somewhere no egress gate authorised, so it is refused before it can ever be stored.
        //
        // Only on a CHANGE to the value, though. Both save paths validate the merged result, so re-validating an
        // unchanged stored value would reject every save of every other setting once the configured fast model is
        // uninstalled — with a message naming a field the operator never touched. The dispatcher re-checks the same
        // pair per turn (enforcement point 2), so an already-stored value that stops being node-local is refused where
        // it would actually be used rather than blocking the settings page.
        if (!string.Equals(settings.AutoEffortFastModelName, current.AutoEffortFastModelName, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(settings.AutoEffortFastModelName)
            && !await IsInstalledNodeLocalModelAsync(settings.AutoEffortFastModelName, cancellationToken).ConfigureAwait(false))
        {
            return NodeSettingsAdministrationResult.Rejected(settings,
            [
                new NodeSettingsValidationError(NodeSettingsField.AutoEffortFastModelName, AutoEffortFastModelNotLocalMessage)
            ]);
        }

        var errors = await NodeSettingsPolicy.ValidateMergedAsync(settings, _runtimeSettings, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return NodeSettingsAdministrationResult.Rejected(settings, errors);
        }

        // Read-modify-write under the store's own lock, never a load here and a save there. The settings file is
        // written WHOLE, so the projection is re-applied to the LATEST record: every field the caller did not supply
        // then comes from what is actually stored rather than from this request's snapshot. MachineKey is the one
        // member no save carries a value for — a key minted between this request's load and this line, since
        // IMachineKeyProvider races every settings save on the same node — so it is taken from the latest record too,
        // ahead of whatever the projection produced, orphaning no frozen profile.
        var persisted = settings;
        await _store.UpdateAsync(latest =>
                     {
                         persisted = apply(latest) with
                         {
                             MachineKey = latest.MachineKey
                         };

                         return persisted;
                     },
                     cancellationToken)
                    .ConfigureAwait(false);
        await TryReportCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return NodeSettingsAdministrationResult.Saved(persisted);
    }

    private async Task TryReportCapabilitiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _capabilityReporter.ReportToApiAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report capabilities after node settings were saved.");
        }
    }

    // Enforcement point 1's predicate. Shared verbatim with enforcement point 2 (the dispatcher's per-turn re-check)
    // so a value this save accepts is exactly a value that turn admits, and the registry membership test is what stops
    // an arbitrary string — a cloud model id included — from passing two resolvers that both default the unknown to
    // "node-local llama.cpp".
    private Task<bool> IsInstalledNodeLocalModelAsync(string modelName, CancellationToken cancellationToken) =>
        NodeLocalModelGate.IsInstalledNodeLocalLlamaModelAsync(modelName,
            _ggufModelStore,
            _modelTrustResolver,
            _localModelProviderResolver,
            cancellationToken);

    private static string? TrimWhenProvided(string? value, string? current) =>
        value is null ? current : value.Trim();

    private static NodeSettingsAgenticView ToAgenticView(StoredNodeSettings settings) =>
        new(settings.DefaultModelName,
            settings.EnableTools,
            settings.ToolCapableModels,
            settings.HuggingFaceDefaultQuant,
            settings.LlamaMaxLoadedProcesses,
            settings.LlamaIdleTimeToLiveSeconds,
            settings.KeepModelWarmEnabled,
            settings.KeepModelWarmModelName,
            settings.KeepModelWarmIntervalSeconds,
            settings.MaxMessageRequestTimeoutSeconds,
            settings.ChatCacheReuse,
            settings.SpeculativeMode,
            settings.SpeculativeDraftModelName,
            settings.SpeculativeDraftMaxTokens,
            settings.SpeculativeDraftGpuLayers,
            settings.KvCacheType,
            settings.RerankerModelName,
            settings.AutoEffortFastModelName);
}
