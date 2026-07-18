namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
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
///     <see cref="INodeAeadCipher.Decrypt" /> throws <see cref="AuthenticationTagMismatchException" /> and the key is
///     never silently accepted with garbage material. In practice a wrong/missing secret fails earlier still — the same
///     operator secret gates the SQLite store — so this path is the last line rather than the only one. This decryptor
///     is read-only over the KEK material and holds no key state of its own, so it needs no dispose.
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

        var valueElement = encryptedElement.Element(NodeDataProtectionKeyRingEncryptor.ValueElementName)
                           ?? throw new InvalidOperationException(
                               $"The encrypted key-ring element is missing its <{NodeDataProtectionKeyRingEncryptor.ValueElementName}> child.");
        var envelope = Convert.FromBase64String((string)valueElement);

        var keyProvider = _services.GetRequiredService<INodeDataProtectionKeyProvider>();
        var cipher = _services.GetRequiredService<INodeAeadCipher>();

        var nonceSize = cipher.NonceSize;
        var tagSize = cipher.TagSize;
        if (envelope.Length < nonceSize + tagSize)
        {
            throw new InvalidOperationException("The encrypted key-ring element is malformed (envelope too short).");
        }

        var nonce = envelope.AsSpan(0, nonceSize);
        var tag = envelope.AsSpan(envelope.Length - tagSize, tagSize);
        var ciphertext = envelope.AsSpan(nonceSize, envelope.Length - nonceSize - tagSize);

        // Throws AuthenticationTagMismatchException on a wrong KEK — the fail-closed guarantee. The recovered plaintext
        // is the key-ring master-key material, so it is zeroed once re-parsed into the element Data Protection consumes.
        var plaintextBytes = cipher.Decrypt(keyProvider.Key.Span, nonce, ciphertext, tag, NodeDataProtectionKeyRingEncryptor.AssociatedData);
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
