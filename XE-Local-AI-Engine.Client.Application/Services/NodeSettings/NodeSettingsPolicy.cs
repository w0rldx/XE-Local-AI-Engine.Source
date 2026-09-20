namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>The stored-settings field a <see cref="NodeSettingsValidationError" /> is attributed to.</summary>
public enum NodeSettingsField
{
    DefaultModelName,
    ToolCapableModels,
    MaxMessageRequestTimeoutSeconds,
    SpeculativeDraftModelName,
    SpeculativeMode,
    SpeculativeDraftMaxTokens,
    SpeculativeDraftGpuLayers,
    KvCacheType,
    ChatCacheReuse,
    LlamaIdleTimeToLiveSeconds,
    KeepModelWarmModelName,
    LlamaMaxLoadedProcesses,
    KeepModelWarmIntervalSeconds,
    AutoEffortFastModelName,
    ContainerRuntimeSelection
}

/// <summary>A single cross-field violation: the offending field plus the operator-facing message.</summary>
public sealed class NodeSettingsValidationError
{
    public required NodeSettingsField Field { get; init; }

    public required string Message { get; init; }
}

/// <summary>
///     Cross-field save policy for node settings, running on the MERGED result of the stored settings and the incoming
///     partial update.
/// </summary>
/// <remarks>
///     That is precisely why the boundary FluentValidation validator cannot express it: the validator sees only the
///     request, so it cannot tell a partial update that enables a feature while keeping an already-stored model from
///     one that enables it with nothing selected. Some rules also need the EFFECTIVE runtime value
///     (stored &gt; appsettings seed &gt; default) for a knob the request omitted, which only
///     <see cref="INodeRuntimeSettings" /> can resolve.
/// </remarks>
public static class NodeSettingsPolicy
{
    /// <summary>
    ///     Validates the merged settings, returning at most one error.
    /// </summary>
    /// <remarks>
    ///     Rules are evaluated in order and evaluation STOPS at the first violation: the caller surfaces one error at a
    ///     time, and a later rule may read runtime state an earlier violation has made meaningless.
    /// </remarks>
    public static async Task<IReadOnlyList<NodeSettingsValidationError>> ValidateMergedAsync(StoredNodeSettings settings,
        INodeRuntimeSettings runtimeSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        // A draft speculative mode with no draft model must never persist: it passes every field-level check and then fails
        // chat-server start on the next spawn. This also covers an update clearing the draft model under a stored draft mode.
        if (StoredNodeSettings.SpeculativeModeRequiresDraftModel(settings.SpeculativeMode)
            && string.IsNullOrWhiteSpace(settings.SpeculativeDraftModelName))
        {
            return
            [
                new NodeSettingsValidationError
                {
                    Field = NodeSettingsField.SpeculativeDraftModelName,
                    Message = "Speculative decoding is set to a draft model mode, but no draft model was selected."
                }
            ];
        }

        // Like the speculative-mode guard above: a partial request may enable the feature while keeping an already-
        // stored model, but can never persist an enabled state with no selected model.
        if (settings.KeepModelWarmEnabled is true
            && string.IsNullOrWhiteSpace(settings.KeepModelWarmModelName))
        {
            return
            [
                new NodeSettingsValidationError
                {
                    Field = NodeSettingsField.KeepModelWarmModelName,
                    Message = "Keep model warm is enabled, but no model was selected."
                }
            ];
        }

        // The FAST model of an `auto` turn is a SECOND chat process alongside the turn's own model, so a node capped
        // at one loaded process could never admit it — the setting would look configured and silently never apply.
        if (!string.IsNullOrWhiteSpace(settings.AutoEffortFastModelName))
        {
            var maxLoadedProcessesForSwap = settings.LlamaMaxLoadedProcesses
                                            ?? await runtimeSettings.GetLlamaMaxLoadedProcessesAsync(cancellationToken);
            if (maxLoadedProcessesForSwap < 2)
            {
                return
                [
                    new NodeSettingsValidationError
                    {
                        Field = NodeSettingsField.LlamaMaxLoadedProcesses,
                        Message = "A fast model for automatic reasoning effort requires at least two loaded-process slots, because it runs alongside the conversation's own model."
                    }
                ];
            }
        }

        if (settings.KeepModelWarmEnabled is not true)
        {
            return [];
        }

        var effectiveMaxLoadedProcesses = settings.LlamaMaxLoadedProcesses
                                          ?? await runtimeSettings.GetLlamaMaxLoadedProcessesAsync(cancellationToken);
        if (effectiveMaxLoadedProcesses < 2)
        {
            return
            [
                new NodeSettingsValidationError
                {
                    Field = NodeSettingsField.LlamaMaxLoadedProcesses,
                    Message = "Keep model warm requires at least two loaded-process slots so another local model can still be admitted."
                }
            ];
        }

        var effectiveKeepWarmInterval = settings.KeepModelWarmIntervalSeconds is { } intervalSeconds
            ? TimeSpan.FromSeconds(intervalSeconds)
            : await runtimeSettings.GetKeepModelWarmIntervalAsync(cancellationToken);
        var effectiveIdleTimeToLive = settings.LlamaIdleTimeToLiveSeconds is { } idleTimeToLiveSeconds
            ? TimeSpan.FromSeconds(idleTimeToLiveSeconds)
            : await runtimeSettings.GetLlamaIdleTimeToLiveAsync(cancellationToken);
        if (effectiveKeepWarmInterval >= effectiveIdleTimeToLive)
        {
            return
            [
                new NodeSettingsValidationError
                {
                    Field = NodeSettingsField.KeepModelWarmIntervalSeconds,
                    Message = "The keep-model-warm interval must be shorter than the llama.cpp idle time-to-live."
                }
            ];
        }

        return [];
    }
}
