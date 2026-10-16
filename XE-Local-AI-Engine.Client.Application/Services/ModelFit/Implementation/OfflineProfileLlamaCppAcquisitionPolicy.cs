namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Refuses an implicit llama.cpp runtime download on a node whose external-access profile is Offline: the Offline card
///     promises the runtime is not fetched automatically, and a chat send used to fetch it anyway.
/// </summary>
public sealed class OfflineProfileLlamaCppAcquisitionPolicy : ILlamaCppImplicitAcquisitionPolicy
{
    internal const string OfflineRefusalMessage =
        "No llama.cpp runtime is installed, and the Offline profile does not download one automatically. Install it in Node Settings under the llama.cpp runtime, or choose another external access profile.";

    private readonly INodeRuntimeSettings _runtimeSettings;

    public OfflineProfileLlamaCppAcquisitionPolicy(INodeRuntimeSettings runtimeSettings)
    {
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    }

    public async Task<string?> GetRefusalAsync(CancellationToken ct)
    {
        var profile = await _runtimeSettings.GetExternalAccessProfileAsync(ct);
        return string.Equals(profile, StoredNodeSettings.ExternalAccessProfileOffline, StringComparison.Ordinal) ? OfflineRefusalMessage : null;
    }
}
