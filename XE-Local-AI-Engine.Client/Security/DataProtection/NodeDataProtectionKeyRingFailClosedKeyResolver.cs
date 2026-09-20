namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.KeyManagement.Internal;

/// <summary>
///     Decorates Data Protection's default <see cref="IDefaultKeyResolver" /> so a SILENT key regeneration caused by an
///     undecryptable key-ring becomes a LOUD, fatal startup failure instead.
/// </summary>
/// <remarks>
///     Applied on BOTH at-rest schemes: the non-Windows AES-GCM wrapper keyed from the node operator secret, and the Windows DPAPI wrapper. The default
///     resolver treats a key whose <see cref="IKey.CreateEncryptor" /> throws as merely ineligible and then generates a fresh one, silently orphaning every
///     <c>IDataProtector</c> payload under the old ring — cloud, Codex, HF, GitHub and worker OAuth tokens, Entra caches. Both wrappers fail all-or-nothing,
///     so one bad key means every key: the non-Windows KEK is derived deterministically from the operator secret, and the Windows DPAPI blobs are all bound
///     to one user profile.
/// </remarks>
public sealed class NodeDataProtectionKeyRingFailClosedKeyResolver : IDefaultKeyResolver
{
    private readonly IDefaultKeyResolver _inner;
    private readonly Func<Exception, bool> _isRingDecryptionFailure;
    private readonly string _remediation;

    /// <summary>
    ///     The non-Windows form: recognises only this node's own distinctive decryption exception, so an
    ///     unrelated <see cref="IKey.CreateEncryptor" /> failure is left to the framework rather than masked as a KEK
    ///     problem.
    /// </summary>
    public NodeDataProtectionKeyRingFailClosedKeyResolver(IDefaultKeyResolver inner)
        : this(inner,
            static exception => exception is NodeDataProtectionKeyRingDecryptionException,
            "Restore the correct operator secret (the same one that unlocks node.sqlite) and restart.")
    {
    }

    /// <summary>
    ///     The classifier and the remediation text are PARAMETERS of the decorator, not properties of one at-rest
    ///     scheme.
    /// </summary>
    /// <remarks>
    ///     Hardcoding <see cref="NodeDataProtectionKeyRingDecryptionException" />, which only the non-Windows encryptor
    ///     throws, made the DPAPI branch inert and left Windows failing open: an unreadable DPAPI ring orphaned every
    ///     <c>*.enc</c> credential with no hard failure and no log line. The decoration is orthogonal to how keys are
    ///     encrypted, because it wraps the RESOLVER. See <see cref="ForDpapiRing" /> for what that branch recognises.
    /// </remarks>
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
    /// </summary>
    /// <remarks>
    ///     Broader than the non-Windows classifier, and it can afford to be: EVERY key in this ring is DPAPI-wrapped by <c>ProtectKeysWithDpapi</c> and unwrapped
    ///     lazily by the very call this decorator probes, so a cryptographic failure materialising a key IS a ring-unwrap failure; matching a framework-internal
    ///     exception type would bind this to a shape the framework does not promise. The remediation is not shared text because there is no secret to restore: a
    ///     CurrentUser blob is bound to the account, so the recoveries are to run as it or to accept the loss and delete the ring.
    /// </remarks>
    public static NodeDataProtectionKeyRingFailClosedKeyResolver ForDpapiRing(IDefaultKeyResolver inner)
    {
        return new NodeDataProtectionKeyRingFailClosedKeyResolver(inner,
            static exception => exception is CryptographicException,
            "The key-ring is DPAPI-protected for the Windows user that created it. Sign in as that user and restart. "
            + "If the ring is genuinely unrecoverable, delete the dp-keys directory to start a new one — every stored "
            + "credential and OAuth token protected under the old ring will have to be entered again.");
    }

    /// <summary>
    ///     Defers to the inner resolver and intervenes only when it has already decided to regenerate AND a non-revoked
    ///     key failed to materialise with a failure this scheme's classifier recognises.
    /// </summary>
    /// <remarks>
    ///     Deliberately conservative, so the resolved-default hot path is untouched. A legacy PLAINTEXT key decrypts
    ///     without invoking any decryptor and never produces a classified failure, so it keeps reading; a
    ///     correctly-decryptable key yields a usable default and is likewise untouched; a genuine first-run or empty
    ///     ring has no failing key and regenerates as before; and a ring whose keys all decrypt but have merely EXPIRED
    ///     regenerates as before too, because no key fails to materialise.
    /// </remarks>
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

    // Probes a key by materializing its encryptor, which unwraps the at-rest key material. True only for a failure this scheme's
    // classifier recognises; success, or one the inner resolver already accounted for, returns false so probing continues unmasked.
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
