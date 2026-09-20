namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     Wraps NEWLY written Data Protection key-ring elements at rest with AES-256-GCM under an operator-secret-derived
///     KEK, registered ONLY on the non-Windows branch.
/// </summary>
/// <remarks>
///     Without it the key-ring XML would sit in plaintext next to the ciphertext it unlocks; Windows keeps DPAPI. The WRITE side only: Data Protection calls <see
///     cref="Encrypt" /> when it persists a new key and records <see cref="NodeDataProtectionKeyRingDecryptor" /> on the element, and NEVER calls it when reading, so a
///     legacy PLAINTEXT element still reads directly, a previously written encrypted one reads through its recorded decryptor, and every existing <c>IDataProtector</c>
///     payload keeps decrypting. AES-GCM is delegated to <see cref="INodeAeadCipher" />, the node's single AEAD owner; the envelope is <c>base64(nonce || ciphertext ||
///     tag)</c> with the KEK version bound as associated data.
/// </remarks>
public sealed class NodeDataProtectionKeyRingEncryptor : IXmlEncryptor
{
    // Bound as AES-GCM associated data so an element wrapped under this scheme can never be replayed as another
    // operator-secret-derived envelope (e.g. a SQLite column value). Versioned to allow a future format change.
    internal static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("c0re-node-dpkeyring|v1");

    internal const string EncryptedElementName = "encryptedKey";
    internal const string ValueElementName = "value";

    private readonly INodeDataProtectionKeyProvider _keyProvider;
    private readonly INodeAeadCipher _cipher;

    public NodeDataProtectionKeyRingEncryptor(INodeDataProtectionKeyProvider keyProvider, INodeAeadCipher cipher)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
    }

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        // Serialize the key element to bytes (formatting stripped so the round-trip is stable). This buffer holds the
        // key-ring master-key material, so it is zeroed the moment the ciphertext exists.
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        try
        {
            var nonce = new byte[_cipher.NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var (ciphertext, tag) = _cipher.Encrypt(_keyProvider.Key.Span, nonce, plaintextBytes, AssociatedData);

            var envelope = new byte[nonce.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(nonce, 0, envelope, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, envelope, nonce.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, envelope, nonce.Length + ciphertext.Length, tag.Length);

            var element = new XElement(EncryptedElementName,
                new XComment(" This key is encrypted at rest with an operator-secret-derived AES-256-GCM key (BE-02). "),
                new XElement(ValueElementName, Convert.ToBase64String(envelope)));

            return new EncryptedXmlInfo(element, typeof(NodeDataProtectionKeyRingDecryptor));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }
}
