namespace XE_Local_AI_Engine.Client.Services.Containers.Bridge;

using System.Text;

/// <summary>
///     What one application container is told about the bridge: where to call, and the credential to call with.
///     Both halves or neither — an endpoint without a token names a surface the container cannot use, and a token
///     without an endpoint names nothing at all.
/// </summary>
/// <param name="Endpoint">The container-facing <c>host:port</c>, which on Docker Desktop is an alias rather than an address.</param>
/// <param name="Token">The instance's own bridge token, in plaintext, on its way into the container's environment.</param>
public sealed record ContainerBridgeGrant(string Endpoint, string Token)
{
    // The token is a live credential for this node's inference surface, so the generated ToString() would put it
    // into any log line that formats a plan. Suppressed the same way the instance snapshot's is, and for the same
    // reason: impossible rather than merely forbidden.
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}
