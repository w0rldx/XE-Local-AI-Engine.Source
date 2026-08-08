namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     AES-256-GCM at-rest protection for the uploaded-file blobs that live on disk (the raw bytes and the cached
///     extracted Markdown), keyed off the same node key material as the encrypted DB columns.
/// </summary>
/// <remarks>
///     The DB-column protector (<c>NodePayloadProtector</c>) is internal to the persistence assembly, so this store —
///     which lives in the application layer and writes blobs to disk outside any DbContext — re-uses the public
///     <see cref="AesGcmNodeAeadCipher" /> primitive and replicates the exact on-disk framing
///     (<c>nonce || ciphertext || tag</c>) and associated-data layout (conversation id, file id, column name, schema
///     version). Distinct column names (<c>file_bytes</c>, <c>file_md</c>) bind each blob to its role so a bytes blob
///     can never be substituted for an extracted-text blob under the same key.
/// </remarks>
internal sealed class UploadedFileBlobProtector
{
    internal const string FileBytesColumn = "file_bytes";
    internal const string FileMarkdownColumn = "file_md";

    private const string SchemaVersion = "v1";

    private static readonly INodeAeadCipher Cipher = new AesGcmNodeAeadCipher();

    private readonly INodeSqliteKeyHolder _keyHolder;

    public UploadedFileBlobProtector(INodeSqliteKeyHolder keyHolder)
    {
        _keyHolder = keyHolder ?? throw new ArgumentNullException(nameof(keyHolder));
    }

    public byte[] Encrypt(Guid conversationId, Guid fileId, string columnName, ReadOnlySpan<byte> plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var nonce = new byte[Cipher.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var aad = BuildAssociatedData(conversationId, fileId, columnName);
        var (ciphertext, tag) = Cipher.Encrypt(_keyHolder.Key.Span, nonce, plaintext, aad);

        var payload = new byte[Cipher.NonceSize + ciphertext.Length + Cipher.TagSize];
        nonce.CopyTo(payload, index: 0);
        ciphertext.CopyTo(payload, Cipher.NonceSize);
        tag.CopyTo(payload, Cipher.NonceSize + ciphertext.Length);
        return payload;
    }

    public byte[] Decrypt(Guid conversationId, Guid fileId, string columnName, ReadOnlySpan<byte> encryptedPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        if (encryptedPayload.Length < Cipher.NonceSize + Cipher.TagSize)
        {
            throw new InvalidOperationException($"Encrypted uploaded-file blob for '{columnName}' is too short.");
        }

        var ciphertextLength = encryptedPayload.Length - Cipher.NonceSize - Cipher.TagSize;
        var aad = BuildAssociatedData(conversationId, fileId, columnName);
        var nonce = encryptedPayload[..Cipher.NonceSize];
        var ciphertext = encryptedPayload.Slice(Cipher.NonceSize, ciphertextLength);
        var tag = encryptedPayload[^Cipher.TagSize..];

        return Cipher.Decrypt(_keyHolder.Key.Span, nonce, ciphertext, tag, aad);
    }

    private static byte[] BuildAssociatedData(Guid conversationId, Guid fileId, string columnName)
    {
        var conversationBytes = conversationId.ToByteArray(bigEndian: true);
        var fileBytes = fileId.ToByteArray(bigEndian: true);
        var columnBytes = Encoding.UTF8.GetBytes(columnName);
        var schemaVersionBytes = Encoding.UTF8.GetBytes(SchemaVersion);

        var aad = new byte[conversationBytes.Length + fileBytes.Length + columnBytes.Length + schemaVersionBytes.Length];
        conversationBytes.CopyTo(aad, index: 0);
        fileBytes.CopyTo(aad, conversationBytes.Length);
        columnBytes.CopyTo(aad, conversationBytes.Length + fileBytes.Length);
        schemaVersionBytes.CopyTo(aad, conversationBytes.Length + fileBytes.Length + columnBytes.Length);
        return aad;
    }
}
