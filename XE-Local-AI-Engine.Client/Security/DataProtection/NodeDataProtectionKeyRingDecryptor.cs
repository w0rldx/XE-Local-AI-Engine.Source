namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     Unwraps key-ring elements written by <see cref="NodeDataProtectionKeyRingEncryptor" /> (BE-02). Data Protection
///     records this type by name on each encrypted element and activates it at key-ring read time through its
///     internal activator, which supplies the application <see cref="IServiceProvider" /> to the single-argument
///     constructor — the same contract the framework's own <c>DpapiXmlDecryptor</c> uses. The KEK and AEAD primitive
///     are resolved from that provider so the operator secret is obtained lazily, at read time, from live DI.
/// </summary>
/// <remarks>
///     Fail-closed: a wrong operator secret yields a KEK that cannot authenticate the GCM tag, so
///     <see cref="INodeAeadCipher.Decrypt" /> throws an <see cref="AuthenticationTagMismatchException" />, which this
///     decryptor re-surfaces as a <see cref="NodeDataProtectionKeyRingDecryptionException" /> (a distinctive
///     <see cref="CryptographicException" />) so <see cref="NodeDataProtectionKeyRingFailClosedKeyResolver" /> can
///     hard-fail startup instead of letting Data Protection silently regenerate the ring. The key is never silently
///     accepted with garbage material. The node store is PLAIN SQLite with application-level COLUMN encryption (not
///     SQLCipher/whole-file), so a wrong secret does not necessarily fail startup — that is exactly why the resolver is
///     the loud backstop rather than "the SQLite store already gates it." This decryptor is read-only over the KEK
///     material and holds no key state of its own, so it needs no dispose.
/// </remarks>
public sealed class NodeDataProtectionKeyRingDecryptor : IXmlDecryptor
{
    private readonly IServiceProvider _services;

    // Data Protection's activator prefers this (IServiceProvider) constructor; the KEK provider and AEAD cipher are
    // resolved on demand in Decrypt so they bind to the live application container at read time.
    public NodeDataProtectionKeyRingDecryptor(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        // Every failure below is surfaced as NodeDataProtectionKeyRingDecryptionException so the fail-closed key
        // resolver (NodeDataProtectionKeyRingFailClosedKeyResolver) can tell an undecryptable ENCRYPTED key apart from
        // an unrelated key failure and hard-fail startup instead of letting Data Protection silently regenerate the ring.
        var valueElement = encryptedElement.Element(NodeDataProtectionKeyRingEncryptor.ValueElementName)
                           ?? throw new NodeDataProtectionKeyRingDecryptionException($"The encrypted key-ring element is missing its <{NodeDataProtectionKeyRingEncryptor.ValueElementName}> child.");
        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String((string)valueElement);
        }
        catch (FormatException formatException)
        {
            throw new NodeDataProtectionKeyRingDecryptionException("The encrypted key-ring element is malformed (its value is not valid base64).", formatException);
        }

        var keyProvider = _services.GetRequiredService<INodeDataProtectionKeyProvider>();
        var cipher = _services.GetRequiredService<INodeAeadCipher>();

        var nonceSize = cipher.NonceSize;
        var tagSize = cipher.TagSize;
        if (envelope.Length < nonceSize + tagSize)
        {
            throw new NodeDataProtectionKeyRingDecryptionException("The encrypted key-ring element is malformed (envelope too short).");
        }

        var nonce = envelope.AsSpan(0, nonceSize);
        var tag = envelope.AsSpan(envelope.Length - tagSize, tagSize);
        var ciphertext = envelope.AsSpan(nonceSize, envelope.Length - nonceSize - tagSize);

        // A wrong KEK (wrong/rotated operator secret) fails the AES-GCM tag here — the fail-closed guarantee. Surface it
        // as our distinctive typed failure (still a CryptographicException) rather than silently accepting garbage. The
        // recovered plaintext is the key-ring master-key material, so it is zeroed once re-parsed into the element.
        byte[] plaintextBytes;
        try
        {
            plaintextBytes = cipher.Decrypt(keyProvider.Key.Span, nonce, ciphertext, tag, NodeDataProtectionKeyRingEncryptor.AssociatedData);
        }
        catch (CryptographicException cryptographicException) when (cryptographicException is not NodeDataProtectionKeyRingDecryptionException)
        {
            throw new NodeDataProtectionKeyRingDecryptionException("The encrypted key-ring element could not be decrypted with the current node operator secret.",
                cryptographicException);
        }

        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintextBytes), LoadOptions.PreserveWhitespace);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }
}
