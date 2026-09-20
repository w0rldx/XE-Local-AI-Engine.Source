namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     Unwraps key-ring elements written by <see cref="NodeDataProtectionKeyRingEncryptor" />.
/// </summary>
/// <remarks>
///     Data Protection records this type by name on each encrypted element and activates it at read time through its internal activator, which supplies
///     the application <see cref="IServiceProvider" /> to the single-argument constructor — the same contract <c>DpapiXmlDecryptor</c> uses — so the KEK
///     and AEAD primitive are resolved lazily from live DI. Fail-closed: a wrong operator secret cannot authenticate the GCM tag, and the resulting <see
///     cref="AuthenticationTagMismatchException" /> is re-surfaced as a <see cref="NodeDataProtectionKeyRingDecryptionException" />, never silently
///     accepted with garbage material. Read-only over the KEK, so it needs no dispose.
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

        // Every failure below is surfaced as NodeDataProtectionKeyRingDecryptionException, so NodeDataProtectionKeyRingFailClosedKeyResolver
        // can tell an undecryptable ENCRYPTED key from an unrelated one and hard-fail startup rather than let the ring be regenerated.
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

        // A wrong or rotated operator secret fails the AES-GCM tag here — the fail-closed guarantee — and is surfaced as the distinctive
        // typed failure (still a CryptographicException). The plaintext is key-ring master-key material, so it is zeroed once re-parsed.
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
