namespace XE_Local_AI_Engine.Client.Services.Images;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;

/// <summary>
///     AES-256-GCM at-rest protection for generated-image blobs written to disk, keyed off the same node key material as
///     the encrypted DB columns.
/// </summary>
/// <remarks>
///     Mirrors <c>UploadedFileBlobProtector</c> exactly: the DB-column protector (<c>NodePayloadProtector</c>) is internal
///     to the persistence assembly, so this store — which lives in the application layer and writes blobs to disk outside
///     any DbContext — re-uses the public <see cref="AesGcmNodeAeadCipher" /> primitive and replicates the on-disk framing
///     (<c>nonce || ciphertext || tag</c>) and associated-data layout (job id, image id, column name, schema version).
///     The AAD binds each blob to its (jobId, imageId, column) so an image blob can never be substituted under the key.
/// </remarks>
internal sealed class ImageBlobProtector
{
    internal const string ImageBytesColumn = "image_bytes";

    private const string SchemaVersion = "v1";

    private static readonly INodeAeadCipher Cipher = new AesGcmNodeAeadCipher();

    private readonly INodeSqliteKeyHolder _keyHolder;

    public ImageBlobProtector(INodeSqliteKeyHolder keyHolder)
    {
        _keyHolder = keyHolder ?? throw new ArgumentNullException(nameof(keyHolder));
    }

    public byte[] Encrypt(Guid jobId, Guid imageId, string columnName, ReadOnlySpan<byte> plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        var nonce = new byte[Cipher.NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var aad = BuildAssociatedData(jobId, imageId, columnName);
        var (ciphertext, tag) = Cipher.Encrypt(_keyHolder.Key.Span, nonce, plaintext, aad);

        var payload = new byte[Cipher.NonceSize + ciphertext.Length + Cipher.TagSize];
        nonce.CopyTo(payload, index: 0);
        ciphertext.CopyTo(payload, Cipher.NonceSize);
        tag.CopyTo(payload, Cipher.NonceSize + ciphertext.Length);
        return payload;
    }

    public byte[] Decrypt(Guid jobId, Guid imageId, string columnName, ReadOnlySpan<byte> encryptedPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        if (encryptedPayload.Length < Cipher.NonceSize + Cipher.TagSize)
        {
            throw new InvalidOperationException($"Encrypted generated-image blob for '{columnName}' is too short.");
        }

        var ciphertextLength = encryptedPayload.Length - Cipher.NonceSize - Cipher.TagSize;
        var aad = BuildAssociatedData(jobId, imageId, columnName);
        var nonce = encryptedPayload[..Cipher.NonceSize];
        var ciphertext = encryptedPayload.Slice(Cipher.NonceSize, ciphertextLength);
        var tag = encryptedPayload[^Cipher.TagSize..];

        return Cipher.Decrypt(_keyHolder.Key.Span, nonce, ciphertext, tag, aad);
    }

    private static byte[] BuildAssociatedData(Guid jobId, Guid imageId, string columnName)
    {
        var jobBytes = jobId.ToByteArray(bigEndian: true);
        var imageBytes = imageId.ToByteArray(bigEndian: true);
        var columnBytes = Encoding.UTF8.GetBytes(columnName);
        var schemaVersionBytes = Encoding.UTF8.GetBytes(SchemaVersion);

        var aad = new byte[jobBytes.Length + imageBytes.Length + columnBytes.Length + schemaVersionBytes.Length];
        jobBytes.CopyTo(aad, index: 0);
        imageBytes.CopyTo(aad, jobBytes.Length);
        columnBytes.CopyTo(aad, jobBytes.Length + imageBytes.Length);
        schemaVersionBytes.CopyTo(aad, jobBytes.Length + imageBytes.Length + columnBytes.Length);
        return aad;
    }
}
