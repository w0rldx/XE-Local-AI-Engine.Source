namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     Wraps NEWLY written Data Protection key-ring elements at rest with AES-256-GCM under an operator-secret-derived
///     KEK (BE-02), so that on non-Windows hosts the key-ring XML is no longer plaintext next to the ciphertext it
///     unlocks. Registered ONLY on the non-Windows branch (Windows keeps DPAPI, unchanged).
/// </summary>
/// <remarks>
///     <para>
///         This is the WRITE side only. Data Protection invokes <see cref="Encrypt" /> when it persists a new key and
///         records <see cref="NodeDataProtectionKeyRingDecryptor" /> as the paired decryptor on the element. It is NEVER
///         invoked when reading existing keys, so adding this encryptor cannot affect already-persisted keys: a legacy
///         PLAINTEXT key element has no encrypted wrapper and Data Protection reads it directly, and any previously
///         written encrypted element is read back through its recorded decryptor. Existing <c>IDataProtector</c> payloads
///         (cloud tokens, Codex/HF/GitHub tokens, the worker auth token, Entra caches) therefore keep decrypting.
///     </para>
///     <para>
///         AES-GCM is delegated to the node's single AEAD owner (<see cref="INodeAeadCipher" />) rather than
///         constructing <c>AesGcm</c> here, preserving the repo's single-AEAD-owner discipline. The on-disk envelope is
///         <c>base64(nonce || ciphertext || tag)</c>; the KEK version is bound as associated data for domain separation.
///     </para>
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
