namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

internal static class LlamaCppPrebuiltRuntimeMutationGuard
{
    internal const string KeepModelWarmBlockedMessage =
        "Disable Keep Model Warm before changing the llama.cpp runtime, then eject any running models and retry.";

    internal static Task<bool> IsKeepModelWarmEnabledAsync(INodeRuntimeSettings nodeRuntimeSettings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nodeRuntimeSettings);
        return nodeRuntimeSettings.GetKeepModelWarmEnabledAsync(ct);
    }
}
