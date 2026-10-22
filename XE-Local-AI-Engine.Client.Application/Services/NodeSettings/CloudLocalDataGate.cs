namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The local-data locality gate graph workflow nodes share: node-local data reaches a model that leaves the node only
///     while its connection grants local data or the node-wide switch allows it.
/// </summary>
/// <remarks>
///     Node-local data here is an uploaded file or a knowledge-base or workspace tool result. A stateless function over <see cref="INodeRuntimeSettings" />, needing no interface, lifetime or registration
///     (same shape as <see cref="CloudUnattendedGate" />). It is the <c>leavesNode &amp;&amp; !grant &amp;&amp; !switch</c>
///     rule of <see cref="ExternalProviderCloudGrants" /> for <see cref="ExternalProviderCloudGrants.LocalData" />.
///     The chat, agent, orchestration and integration paths call it too; a caller that folds several models into one
///     <c>leavesNode</c> passes <see cref="ExternalProviderCloudGrants.None" />.
/// </remarks>
public static class CloudLocalDataGate
{
    /// <summary>
    ///     <see langword="true" /> when the local data must be withheld: the model leaves the node, its connection does
    ///     not grant local data and <c>AllowCloudModelAccess</c> is off.
    /// </summary>
    public static async Task<bool> WithholdsAsync(bool leavesNode,
        ExternalProviderCloudGrants grants,
        INodeRuntimeSettings runtimeSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        return leavesNode && !grants.LocalData && !await runtimeSettings.GetAllowCloudModelAccessAsync(cancellationToken);
    }

    /// <summary>
    ///     Synchronous twin of <see cref="WithholdsAsync" /> for the synchronous tool-offer seam.
    /// </summary>
    public static bool Withholds(bool leavesNode, ExternalProviderCloudGrants grants, INodeRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        return leavesNode && !grants.LocalData && !runtimeSettings.GetAllowCloudModelAccess();
    }
}
