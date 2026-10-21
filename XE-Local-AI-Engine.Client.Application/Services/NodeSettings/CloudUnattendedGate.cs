namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The one unattended-run locality gate the scheduler, graph workflow nodes, graph validation and integration
///     runs share: a model that leaves the node runs unattended only while its connection grants it or the node-wide
///     switch allows it.
/// </summary>
/// <remarks>
///     A stateless function over <see cref="INodeRuntimeSettings" />, needing no interface, lifetime or registration
///     (same shape as <see cref="ExternalAccessGate" />). It is the <c>leavesNode &amp;&amp; !grant &amp;&amp; !switch</c>
///     rule of <see cref="ExternalProviderCloudGrants" /> for <see cref="ExternalProviderCloudGrants.UnattendedRuns" />;
///     every caller used to spell it inline, and a later change to the rule had to be made five times.
/// </remarks>
public static class CloudUnattendedGate
{
    /// <summary>
    ///     <see langword="true" /> when the run must be refused: the model leaves the node, its connection does not
    ///     grant unattended runs and <c>AllowCloudModelUnattendedRuns</c> is off.
    /// </summary>
    public static async Task<bool> WithholdsAsync(bool leavesNode,
        ExternalProviderCloudGrants grants,
        INodeRuntimeSettings runtimeSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        return leavesNode && !grants.UnattendedRuns && !await runtimeSettings.GetAllowCloudModelUnattendedRunsAsync(cancellationToken);
    }
}
