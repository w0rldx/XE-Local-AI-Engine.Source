namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

using System.Security.Cryptography;
using System.Text;

internal static class NodePayloadProtector
{
    private const string SchemaVersion = "v1";

    private static readonly INodeAeadCipher Cipher = new AesGcmNodeAeadCipher();

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        Guid conversationId,
        Guid recordId,
        string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var nonce = new byte[Cipher.NonceSize];
        var aad = BuildAssociatedData(conversationId, recordId, columnName);

        RandomNumberGenerator.Fill(nonce);

        var (ciphertext, tag) = Cipher.Encrypt(key, nonce, plaintext, aad);

        var payload = new byte[Cipher.NonceSize + ciphertext.Length + Cipher.TagSize];
        nonce.CopyTo(payload, index: 0);
        ciphertext.CopyTo(payload, Cipher.NonceSize);
        tag.CopyTo(payload, Cipher.NonceSize + ciphertext.Length);
        return payload;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> encryptedPayload,
        ReadOnlySpan<byte> key,
        Guid conversationId,
        Guid recordId,
        string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        if (encryptedPayload.Length < Cipher.NonceSize + Cipher.TagSize)
        {
            throw new InvalidOperationException($"Encrypted payload for column '{columnName}' is too short.");
        }

        var ciphertextLength = encryptedPayload.Length - Cipher.NonceSize - Cipher.TagSize;
        var aad = BuildAssociatedData(conversationId, recordId, columnName);
        var nonce = encryptedPayload[..Cipher.NonceSize];
        var ciphertext = encryptedPayload.Slice(Cipher.NonceSize, ciphertextLength);
        var tag = encryptedPayload[^Cipher.TagSize..];

        return Cipher.Decrypt(key, nonce, ciphertext, tag, aad);
    }

    private static byte[] BuildAssociatedData(Guid conversationId, Guid recordId, string columnName)
    {
        var conversationBytes = conversationId.ToByteArray(true);
        var recordBytes = recordId.ToByteArray(true);
        var columnBytes = Encoding.UTF8.GetBytes(columnName);
        var schemaVersionBytes = Encoding.UTF8.GetBytes(SchemaVersion);
        var aad = new byte[conversationBytes.Length + recordBytes.Length + columnBytes.Length + schemaVersionBytes.Length];

        conversationBytes.CopyTo(aad, index: 0);
        recordBytes.CopyTo(aad, conversationBytes.Length);
        columnBytes.CopyTo(aad, conversationBytes.Length + recordBytes.Length);
        schemaVersionBytes.CopyTo(aad, conversationBytes.Length + recordBytes.Length + columnBytes.Length);

        return aad;
    }
}
