namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;

/// <summary>
///     Verifies a presented bridge token against the instance it claims.
///     <para>
///         This is the direction the layering allows: External Apps knows the bridge's seam, the bridge knows
///         nothing about External Apps. It lives here because the token is a fact about an installed application —
///         minted at install, dead when the row is deleted — and the row is this feature's.
///     </para>
///     <para>
///         Every failure returns the same nothing and logs nothing distinguishing. A caller able to tell "no such
///         instance" from "wrong secret" learns which instance ids exist, which is the one thing the id half of the
///         token was not meant to reveal.
///     </para>
/// </summary>
internal sealed class ExternalAppBridgeTokenVerifier : IContainerBridgeTokenVerifier
{
    private readonly IExternalAppInstanceStore _store;

    public ExternalAppBridgeTokenVerifier(IExternalAppInstanceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ContainerBridgeCaller?> VerifyAsync(string presentedToken, CancellationToken cancellationToken = default)
    {
        // The id half is a routing hint, never a credential: it turns verification into one keyed row read instead of
        // a comparison against every installed application's secret.
        if (!ContainerBridgeToken.TryParse(presentedToken, out var instanceId, out _))
        {
            return null;
        }

        var row = await _store.GetAsync(instanceId, cancellationToken);
        if (row?.BridgeToken is not { Length: > 0 } stored)
        {
            // No such instance, or one installed before the bridge existed. Neither has bridge access.
            return null;
        }

        var expected = Encoding.UTF8.GetBytes(stored);
        var candidate = Encoding.UTF8.GetBytes(presentedToken);
        try
        {
            // Whole token, not just the secret half: comparing the full string binds the id to the secret, so a token
            // whose id was rewritten to name another instance cannot match that instance's row either.
            return CryptographicOperations.FixedTimeEquals(expected, candidate)
                ? new ContainerBridgeCaller(instanceId)
                : null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
            CryptographicOperations.ZeroMemory(expected);
        }
    }
}
