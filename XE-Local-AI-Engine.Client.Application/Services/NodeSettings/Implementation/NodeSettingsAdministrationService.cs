namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using System.Text.Json;
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

    /// <summary>
    ///     How many times a save re-validates against a record that changed under it before it gives up and refuses.
    ///     A ceiling is needed at all because the alternative is a save that spins for as long as any other writer
    ///     keeps touching this file.
    /// </summary>
    private const int MaxSaveAttempts = 3;

    // Comparison-only serializer for the write-time conflict check below. Its settings are irrelevant as long as both
    // sides use the same instance; it is deliberately NOT the store's (private) one.
    private static readonly JsonSerializerOptions ComparisonSerializerOptions = new(JsonSerializerDefaults.Web);

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

    public async Task<NodeSettingsAdministrationResult> SaveTrustedMergedAsync(Func<StoredNodeSettings, StoredNodeSettings> merge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(merge);
        var current = await GetTrustedSettingsAsync(cancellationToken).ConfigureAwait(false);

        // The MERGE, not a merged record: the wire DTO looks whole but is optional field by optional field, so the
        // caller resolves every omitted one from the record it is handed. Handing it a pre-validation snapshot made a
        // request that changes one knob write that snapshot's value back over every field a sibling writer had
        // changed in the window — a tool-capable-model registration, a default-model selection. Re-applied to the
        // write-time record below, an omitted field keeps what is actually stored.
        //
        // LOCAL-ONLY members ride along from that record instead of from the caller. MachineKey is minted node-side by
        // IMachineKeyProvider and is deliberately absent from the wire DTO, so a caller that builds a
        // StoredNodeSettings out of a request has no value to supply and saving its record verbatim would erase the
        // key. That is silent data loss: the next start mints a fresh key, and every frozen inference profile — keyed
        // by machine key — is orphaned while still reading as frozen. Applied here rather than only at the
        // persistence boundary in ValidateAndSaveAsync so the record this call VALIDATES and RETURNS carries the key
        // too, including on the rejection paths that never reach a write.
        return await ValidateAndSaveAsync(record => merge(record) with
            {
                MachineKey = record.MachineKey
            },
            current,
            cancellationToken).ConfigureAwait(false);
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

        // The default model the cache invalidation below treats as the PREVIOUS one, captured inside the projection
        // rather than read off the snapshot: the transition to invalidate is the one that actually happened on disk,
        // and the record the write lands on is not necessarily the one this call validated. The projection runs more
        // than once per save (the validation preview, then the write, and once more per re-validation), and each run
        // overwrites this — which is what makes the surviving value the one from the invocation that was persisted.
        var previousDefaultModelName = current.DefaultModelName;

        // The projection, not its result: a PARTIAL patch names only the fields it supplies, so every other field must
        // come from the record the write actually lands on. Applying this to the snapshot loaded above and saving THAT
        // whole record wrote back the snapshot's value for every unnamed field, silently reverting any sibling writer
        // — a tool-capable-model registration, a default-model selection — that landed while this call validated.
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

        var result = await ValidateAndSaveAsync(Apply, current, cancellationToken).ConfigureAwait(false);
        if (result.Updated && patch.DefaultModelName is not null)
        {
            await _defaultModelSelectionPolicy
                  .InvalidateCacheForTransitionAsync(previousDefaultModelName, result.Settings.DefaultModelName, cancellationToken)
                  .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    ///     Validates the record <paramref name="apply" /> produces from the loaded snapshot, then persists the SAME
    ///     projection re-applied to the record the store holds at write time, re-validating when that record turns out
    ///     to have changed under the validation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Validation necessarily runs against a snapshot: the policy checks are async (they resolve models) and
    ///         the store's mutation must stay pure and synchronous under its lock. The write therefore re-applies
    ///         <paramref name="apply" /> rather than saving the validated preview, so fields the caller never supplied
    ///         come from the write-time record instead of a stale copy.
    ///     </para>
    ///     <para>
    ///         Rebasing onto a record that moved can compose two individually valid updates into an invalid one — a
    ///         patch that validated "keep model warm on" against a stored warm model, rebased onto a sibling write
    ///         that cleared that model, persists keep-warm enabled with nothing selected. So the mutation compares the
    ///         write-time record with the one that was validated and declines to project on a difference; this method
    ///         then reloads, re-validates and tries again, up to <see cref="MaxSaveAttempts" /> times. After that many
    ///         conflicts in a row the save is REFUSED and the caller gets a conflict result: nothing is ever written
    ///         that was not validated against the record it landed on.
    ///     </para>
    ///     <para>
    ///         <paramref name="apply" /> is therefore invoked several times per save, and a caller that captures a
    ///         value out of it gets the LAST invocation's value rather than an accumulation of all of them: each run
    ///         overwrites the captured local, and the run that produced the persisted record is the last one.
    ///     </para>
    /// </remarks>
    private async Task<NodeSettingsAdministrationResult> ValidateAndSaveAsync(Func<StoredNodeSettings, StoredNodeSettings> apply,
        StoredNodeSettings current,
        CancellationToken cancellationToken)
    {
        var validatedAgainst = current;
        for (var attempt = 1; attempt <= MaxSaveAttempts; attempt++)
        {
            var settings = apply(validatedAgainst);

            // Enforcement point 1 of the node-locality gate, on BOTH save paths (the endpoint's merged save and the
            // MCP patch) rather than only the patch: the runner's dispatcher may move an `auto` turn onto this model,
            // and the turn's data was admitted upstream against a node-local one. A cloud id, an `ext:` id or an
            // Ollama name would carry that data somewhere no egress gate authorised, so it is refused before it can
            // ever be stored.
            //
            // Only on a CHANGE to the value, though. Both save paths validate the merged result, so re-validating an
            // unchanged stored value would reject every save of every other setting once the configured fast model is
            // uninstalled — with a message naming a field the operator never touched. The dispatcher re-checks the
            // same pair per turn (enforcement point 2), so an already-stored value that stops being node-local is
            // refused where it would actually be used rather than blocking the settings page.
            if (!string.Equals(settings.AutoEffortFastModelName, validatedAgainst.AutoEffortFastModelName, StringComparison.Ordinal)
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
            // written WHOLE, so the projection is re-applied to the LATEST record: every field the caller did not
            // supply then comes from what is actually stored rather than from this request's snapshot. MachineKey is
            // the one member no save carries a value for — a key minted between this request's load and this line,
            // since IMachineKeyProvider races every settings save on the same node — so it is taken from the latest
            // record too, ahead of whatever the projection produced, orphaning no frozen profile.
            var changedUnderTheValidation = false;
            var persisted = await _store.UpdateAsync(latest =>
                                            {
                                                changedUnderTheValidation = !SameExceptMachineKey(latest, validatedAgainst);
                                                if (changedUnderTheValidation)
                                                {
                                                    // Nothing may be projected onto a record this attempt never
                                                    // validated — on the last attempt as much as on the first. Returning
                                                    // `latest` unchanged still costs one redundant write, because
                                                    // UpdateAsync always persists — that is the price of reading the
                                                    // write-time record under the store's own lock, and the file it
                                                    // rewrites is byte-identical.
                                                    return latest;
                                                }

                                                return apply(latest) with
                                                {
                                                    MachineKey = latest.MachineKey
                                                };
                                            },
                                            cancellationToken)
                                        .ConfigureAwait(false);

            if (changedUnderTheValidation)
            {
                validatedAgainst = await GetTrustedSettingsAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await TryReportCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            return NodeSettingsAdministrationResult.Saved(persisted);
        }

        // ponytail: the ceiling is a fixed attempt count, and reaching it refuses the save rather than serializing
        // the writers. A version/etag on INodeSettingsStore.UpdateAsync would be the upgrade if a real workload ever
        // hits this.
        _logger.LogWarning("Node settings changed under this save on all {AttemptLimit} attempts. Nothing was written.",
            MaxSaveAttempts);
        return NodeSettingsAdministrationResult.Conflict(validatedAgainst);
    }

    // Whether the write-time record still is the one that was validated. MachineKey is excluded because the projection
    // above already takes it from the write-time record, so a key minted in the window is not a conflict to resolve.
    //
    // Serialize-and-compare rather than the record's own equality: StoredNodeSettings holds an IReadOnlyList<string>
    // and nested records, whose compiler-generated equality is by REFERENCE, so two loads of the same stored
    // allow-list would read as a change and burn every attempt on a difference that does not exist.
    // ponytail: two serializations of a tiny record per save attempt; a hand-written comparer if it ever shows up in
    // a profile.
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
