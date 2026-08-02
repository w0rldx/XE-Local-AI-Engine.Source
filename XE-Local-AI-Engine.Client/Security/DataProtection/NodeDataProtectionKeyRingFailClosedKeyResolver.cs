namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.KeyManagement.Internal;

/// <summary>
///     Decorates Data Protection's default <see cref="IDefaultKeyResolver" /> so a SILENT key regeneration caused by an
///     undecryptable key-ring becomes a LOUD, fatal startup failure instead. Applied on BOTH at-rest schemes: the
///     non-Windows AES-GCM wrapper keyed from the node operator secret (BE-02), and the Windows DPAPI wrapper.
/// </summary>
/// <remarks>
///     <para>
///         Data Protection's default resolver treats a key whose <see cref="IKey.CreateEncryptor" /> throws as merely
///         ineligible; finding no usable default it then decides to generate a fresh key — silently orphaning every
///         existing <c>IDataProtector</c> payload (cloud / Codex / HF / GitHub / worker OAuth tokens, Entra caches)
///         protected under the old ring. Both wrappers fail all-or-nothing, so one bad key means every key: on
///         non-Windows the KEK is derived deterministically from the operator secret, and on Windows the DPAPI blobs
///         are all bound to the same user profile. This decorator detects exactly that case — a regeneration is pending
///         AND a non-revoked key failed to materialise with a failure the scheme's own classifier recognises — and
///         throws, so the operator sees the real cause rather than a quietly reset key-ring.
///     </para>
///     <para>
///         <b>Why the Windows branch was left out originally, and why that was wrong.</b> The classifier was hardcoded
///         to <see cref="NodeDataProtectionKeyRingDecryptionException" />, which only the non-Windows encryptor throws,
///         so decorating the DPAPI branch would have been inert. That is a reason to make the classifier a parameter,
///         not a reason to leave Windows failing open: the decoration is orthogonal to how keys are encrypted — it
///         wraps the RESOLVER — and an unreadable DPAPI ring orphaned every <c>*.enc</c> credential with no hard
///         failure and no log line. See <see cref="ForDpapiRing" /> for what that branch recognises and why it can
///         afford to be broader.
///     </para>
///     <para>
///         It is deliberately conservative and defers to the inner resolver for the decision. It only intervenes when
///         the inner resolver has already decided to regenerate (<see cref="DefaultKeyResolution.ShouldGenerateNewKey" />),
///         so the resolved-default hot path is untouched. A legacy PLAINTEXT key (no wrapper) decrypts without invoking
///         any decryptor and never produces a classified failure, so it keeps reading normally; a correctly-decryptable
///         key yields a usable default (no regeneration) and is likewise untouched; a genuine first-run/empty ring has
///         no failing key and regenerates as before; and a ring whose keys all decrypt but have merely EXPIRED
///         regenerates as before too, because no key fails to materialise.
///     </para>
/// </remarks>
public sealed class NodeDataProtectionKeyRingFailClosedKeyResolver : IDefaultKeyResolver
{
    private readonly IDefaultKeyResolver _inner;
    private readonly Func<Exception, bool> _isRingDecryptionFailure;
    private readonly string _remediation;

    /// <summary>
    ///     The non-Windows (BE-02) form: recognises only this node's own distinctive decryption exception, so an
    ///     unrelated <see cref="IKey.CreateEncryptor" /> failure is left to the framework rather than masked as a KEK
    ///     problem.
    /// </summary>
    public NodeDataProtectionKeyRingFailClosedKeyResolver(IDefaultKeyResolver inner)
        : this(inner,
            static exception => exception is NodeDataProtectionKeyRingDecryptionException,
            "Restore the correct operator secret (the same one that unlocks node.sqlite) and restart.")
    {
    }

    private NodeDataProtectionKeyRingFailClosedKeyResolver(IDefaultKeyResolver inner,
        Func<Exception, bool> isRingDecryptionFailure,
        string remediation)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _isRingDecryptionFailure = isRingDecryptionFailure ?? throw new ArgumentNullException(nameof(isRingDecryptionFailure));
        _remediation = remediation ?? throw new ArgumentNullException(nameof(remediation));
    }

    /// <summary>
    ///     The Windows DPAPI form: recognises a <see cref="CryptographicException" /> anywhere in the failure chain,
    ///     which is what <c>ProtectedData.Unprotect</c> raises when the blob cannot be unwrapped for the current user.
    ///     <para>
    ///         Broader than the non-Windows classifier, and it can afford to be: on this branch EVERY key in the ring
    ///         is DPAPI-wrapped by <c>ProtectKeysWithDpapi</c>, and the wrapper is unwrapped lazily by the very call
    ///         this decorator probes — so a cryptographic failure materialising a key is a ring-unwrap failure. The
    ///         alternative, matching a framework-internal exception type, would bind this to a shape the framework does
    ///         not promise.
    ///     </para>
    ///     <para>
    ///         Note the remediation genuinely differs from the non-Windows one and that is why it is not shared text.
    ///         There is no secret to restore here: a DPAPI CurrentUser blob is bound to the Windows account, so the
    ///         recoveries are to run as that account again, or to accept the loss and delete the ring.
    ///     </para>
    /// </summary>
    public static NodeDataProtectionKeyRingFailClosedKeyResolver ForDpapiRing(IDefaultKeyResolver inner)
    {
        return new NodeDataProtectionKeyRingFailClosedKeyResolver(inner,
            static exception => exception is CryptographicException,
            "The key-ring is DPAPI-protected for the Windows user that created it. Sign in as that user and restart. "
            + "If the ring is genuinely unrecoverable, delete the dp-keys directory to start a new one — every stored "
            + "credential and OAuth token protected under the old ring will have to be entered again.");
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
                throw new InvalidOperationException($"Data Protection key '{key.KeyId}' is encrypted at rest but could not be decrypted. "
                                                    + "Refusing to regenerate the key-ring, which would silently orphan every stored credential and OAuth token. "
                                                    + _remediation,
                    failure);
            }
        }

        return resolution;
    }

    // Probes a key by materializing its encryptor (which unwraps the at-rest key material). Returns true only when the
    // failure is one this scheme's classifier recognises; any other outcome (success, or an unrelated failure the inner
    // resolver already accounted for) returns false so probing continues without masking it as a ring problem.
    private bool TryDetectKeyRingDecryptionFailure(IKey key, out Exception failure)
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

    // The classifier is applied along the whole chain: the framework wraps a decryptor's exception before it reaches
    // here, so matching only the outermost type would never fire.
    private bool IsKeyRingDecryptionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (_isRingDecryptionFailure(current))
            {
                return true;
            }
        }

        return false;
    }
}
