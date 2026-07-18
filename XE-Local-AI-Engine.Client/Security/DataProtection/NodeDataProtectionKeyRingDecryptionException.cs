namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;

/// <summary>
///     Raised by <see cref="NodeDataProtectionKeyRingDecryptor" /> when an ENCRYPTED Data Protection key-ring element
///     (BE-02) cannot be unwrapped — a wrong/rotated node operator secret (KEK) or a corrupt envelope. It derives from
///     <see cref="CryptographicException" /> so the fail-closed posture is preserved for any catch-all, and it is a
///     DISTINCTIVE type so <see cref="NodeDataProtectionKeyRingFailClosedKeyResolver" /> can tell an undecryptable
///     encrypted key apart from an unrelated key failure and hard-fail startup rather than let Data Protection silently
///     regenerate the ring (which would orphan every existing <c>IDataProtector</c> payload).
/// </summary>
public sealed class NodeDataProtectionKeyRingDecryptionException : CryptographicException
{
    public NodeDataProtectionKeyRingDecryptionException(string message)
        : base(message)
    {
    }

    public NodeDataProtectionKeyRingDecryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
