namespace XE_Local_AI_Engine.Client.Persistence.Cryptography;

using System.Security.Cryptography;
using System.Text;

internal static class NodePayloadProtector
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const string SchemaVersion = "v1";

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        Guid conversationId,
        Guid recordId,
        string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var nonce = new byte[NonceLength];
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        var aad = BuildAssociatedData(conversationId, recordId, columnName);

        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(key, TagLength);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        var payload = new byte[NonceLength + ciphertext.Length + TagLength];
        nonce.CopyTo(payload, 0);
        ciphertext.CopyTo(payload, NonceLength);
        tag.CopyTo(payload, NonceLength + ciphertext.Length);
        return payload;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> encryptedPayload,
        ReadOnlySpan<byte> key,
        Guid conversationId,
        Guid recordId,
        string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        if (encryptedPayload.Length < NonceLength + TagLength)
        {
            throw new InvalidOperationException($"Encrypted payload for column '{columnName}' is too short.");
        }

        var ciphertextLength = encryptedPayload.Length - NonceLength - TagLength;
        var plaintext = new byte[ciphertextLength];
        var aad = BuildAssociatedData(conversationId, recordId, columnName);
        var nonce = encryptedPayload[..NonceLength];
        var ciphertext = encryptedPayload.Slice(NonceLength, ciphertextLength);
        var tag = encryptedPayload[^TagLength..];

        using var aesGcm = new AesGcm(key, TagLength);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);

        return plaintext;
    }

    private static byte[] BuildAssociatedData(Guid conversationId, Guid recordId, string columnName)
    {
        var conversationBytes = conversationId.ToByteArray(true);
        var recordBytes = recordId.ToByteArray(true);
        var columnBytes = Encoding.UTF8.GetBytes(columnName);
        var schemaVersionBytes = Encoding.UTF8.GetBytes(SchemaVersion);
        var aad = new byte[conversationBytes.Length + recordBytes.Length + columnBytes.Length + schemaVersionBytes.Length];

        conversationBytes.CopyTo(aad, 0);
        recordBytes.CopyTo(aad, conversationBytes.Length);
        columnBytes.CopyTo(aad, conversationBytes.Length + recordBytes.Length);
        schemaVersionBytes.CopyTo(aad, conversationBytes.Length + recordBytes.Length + columnBytes.Length);

        return aad;
    }
}
