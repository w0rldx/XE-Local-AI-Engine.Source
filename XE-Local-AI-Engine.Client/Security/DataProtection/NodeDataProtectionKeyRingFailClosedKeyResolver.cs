namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.KeyManagement.Internal;

/// <summary>
///     Decorates Data Protection's default <see cref="IDefaultKeyResolver" /> (non-Windows, BE-02) so a SILENT key
///     regeneration caused by an undecryptable ENCRYPTED key becomes a LOUD, fatal startup failure instead.
/// </summary>
/// <remarks>
///     <para>
///         Data Protection's default resolver treats a key whose <see cref="IKey.CreateEncryptor" /> throws as merely
///         ineligible; finding no usable default it then decides to generate a fresh key — silently orphaning every
///         existing <c>IDataProtector</c> payload (cloud / Codex / HF / GitHub / worker OAuth tokens, Entra caches)
///         protected under the old ring. On this node the KEK is derived deterministically from the operator secret,
///         so a wrong/missing secret makes EVERY encrypted key fail to decrypt at once. This decorator detects exactly
///         that case — a regeneration is pending AND a non-revoked key failed with our distinctive
///         <see cref="NodeDataProtectionKeyRingDecryptionException" /> — and throws, so the operator sees the real
///         cause (a bad operator secret) rather than a quietly reset key-ring.
///     </para>
///     <para>
///         It is deliberately conservative and defers to the inner resolver for the decision. It only intervenes when
///         the inner resolver has already decided to regenerate (<see cref="DefaultKeyResolution.ShouldGenerateNewKey" />),
///         so the resolved-default hot path is untouched. A legacy PLAINTEXT key (no encrypted wrapper) decrypts
///         without invoking our decryptor and never produces the distinctive exception, so it keeps reading normally;
///         a correctly-decryptable encrypted key yields a usable default (no regeneration) and is likewise untouched;
///         a genuine first-run/empty ring has no failing key and regenerates as before. A non-decryption
///         <see cref="IKey.CreateEncryptor" /> failure is left to the framework's own handling rather than masked as a
///         KEK problem.
///     </para>
/// </remarks>
public sealed class NodeDataProtectionKeyRingFailClosedKeyResolver : IDefaultKeyResolver
{
    private readonly IDefaultKeyResolver _inner;

    public NodeDataProtectionKeyRingFailClosedKeyResolver(IDefaultKeyResolver inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public DefaultKeyResolution ResolveDefaultKeyPolicy(DateTimeOffset now, IEnumerable<IKey> allKeys)
    {
        ArgumentNullException.ThrowIfNull(allKeys);

        var resolution = _inner.ResolveDefaultKeyPolicy(now, allKeys);

        // Only a pending regeneration can silently orphan the ring; a resolved default key means nothing is being
        // reset, so leave that (hot) path exactly as the inner resolver returned it.
        if (!resolution.ShouldGenerateNewKey)
        {
            return resolution;
        }

        foreach (var key in allKeys)
        {
            // A revoked key was deliberately retired by the operator — its ineligibility is expected, not a decrypt
            // failure to guard against.
            if (key.IsRevoked)
            {
                continue;
            }

            if (TryDetectKeyRingDecryptionFailure(key, out var failure))
            {
                throw new InvalidOperationException(
                    $"Data Protection key '{key.KeyId}' is encrypted at rest (BE-02) but could not be decrypted with the current node operator secret. "
                    + "Refusing to regenerate the key-ring, which would silently orphan every stored credential and OAuth token. "
                    + "Restore the correct operator secret (the same one that unlocks node.sqlite) and restart.",
                    failure);
            }
        }

        return resolution;
    }

    // Probes a key by materializing its encryptor (which decrypts the wrapped key material). Returns true only when the
    // failure is OUR distinctive key-ring decryption exception; any other outcome (success, or an unrelated failure the
    // inner resolver already accounted for) returns false so probing continues without masking it as a KEK problem.
    private static bool TryDetectKeyRingDecryptionFailure(IKey key, out Exception failure)
    {
        try
        {
            _ = key.CreateEncryptor();
        }
        catch (Exception exception)
        {
            if (IsKeyRingDecryptionFailure(exception))
            {
                failure = exception;
                return true;
            }
        }

        failure = null!;
        return false;
    }

    private static bool IsKeyRingDecryptionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NodeDataProtectionKeyRingDecryptionException)
            {
                return true;
            }
        }

        return false;
    }
}
