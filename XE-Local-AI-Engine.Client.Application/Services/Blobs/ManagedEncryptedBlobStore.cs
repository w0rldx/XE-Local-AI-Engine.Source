namespace XE_Local_AI_Engine.Client.Services.Blobs;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions;

internal enum ManagedBlobReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

internal sealed record ManagedBlobWriteResult(string OpaqueReference, string ContentHash, long ByteCount);

internal sealed record ManagedBlobReadResult(ManagedBlobReadStatus Status, ReadOnlyMemory<byte> Content);

/// <summary>
///     The shared body behind the managed blob conventions: AES-GCM under the node key, an AAD binding scope id, blob id
///     and a per-convention column name, write-verify-then-atomic-rename, and immutability (re-writing identical content
///     succeeds, different content throws).
///     <para>
///         The layout is <c>{root}/{folder}/{leaf}/{scopeId:N}/{blobId:N}.blob</c> — the shape Development Mode's
///         artifacts already use on disk, kept byte-identical so its existing blobs stay readable.
///     </para>
/// </summary>
internal sealed class ManagedEncryptedBlobStore
{
    private const string SchemaVersion = "v1";

    private static readonly INodeAeadCipher Cipher = new AesGcmNodeAeadCipher();

    private readonly string _aadColumn;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly string _folderSegment;
    private readonly INodeSqliteKeyHolder _keyHolder;
    private readonly string _leafSegment;
    private readonly int _maxBytes;
    private readonly string _subject;

    public ManagedEncryptedBlobStore(INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        string folderSegment,
        string leafSegment,
        string aadColumn,
        int maxBytes,
        string subject)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _keyHolder = keyHolder ?? throw new ArgumentNullException(nameof(keyHolder));
        _folderSegment = folderSegment;
        _leafSegment = leafSegment;
        _aadColumn = aadColumn;
        _maxBytes = maxBytes;
        _subject = subject;
    }

    public async Task<ManagedBlobWriteResult> WriteAsync(Guid scopeId, Guid blobId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        if (content.Length > _maxBytes)
        {
            throw new InvalidOperationException($"The {_subject} exceeds the configured {_maxBytes}-byte limit.");
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(content.Span));
        var finalPath = BlobPath(scopeId, blobId);
        var directory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException($"The managed {_subject} directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var tempPath = string.Concat(finalPath, ".", Guid.NewGuid().ToString("N"), ".tmp");

        if (File.Exists(finalPath))
        {
            return await VerifyExistingWriteAsync(scopeId, blobId, finalPath, content, contentHash, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var encrypted = Encrypt(scopeId, blobId, content.Span);
            await File.WriteAllBytesAsync(tempPath, encrypted, cancellationToken).ConfigureAwait(false);

            var verifiedEncrypted = await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
            var verifiedPlaintext = Decrypt(scopeId, blobId, verifiedEncrypted);
            var verifiedHash = Convert.ToHexString(SHA256.HashData(verifiedPlaintext));
            if (verifiedPlaintext.LongLength != content.Length || !string.Equals(verifiedHash, contentHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The temporary {_subject} failed hash/size verification.");
            }

            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                return await VerifyExistingWriteAsync(scopeId, blobId, finalPath, content, contentHash, cancellationToken).ConfigureAwait(false);
            }

            return new ManagedBlobWriteResult(OpaqueReference(scopeId, blobId), contentHash, content.Length);
        }
        catch
        {
            DeleteIfPresent(tempPath);
            throw;
        }
    }

    public async Task<ManagedBlobReadResult> ReadAsync(Guid scopeId,
        Guid blobId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        var path = BlobPath(scopeId, blobId);
        if (!File.Exists(path))
        {
            return Failure(ManagedBlobReadStatus.Missing);
        }

        byte[] plaintext;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            plaintext = Decrypt(scopeId, blobId, encrypted);
        }
        catch (Exception exception) when (exception is AuthenticationTagMismatchException or CryptographicException or InvalidDataException)
        {
            return Failure(ManagedBlobReadStatus.Tampered);
        }

        if (plaintext.LongLength != expectedByteCount)
        {
            return Failure(ManagedBlobReadStatus.SizeMismatch);
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(plaintext));
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)
            ? new ManagedBlobReadResult(ManagedBlobReadStatus.Found, plaintext)
            : Failure(ManagedBlobReadStatus.HashMismatch);
    }

    /// <summary>Best-effort removal of one blob. A blob that cannot be removed is left for a later orphan sweep.</summary>
    public void Delete(Guid scopeId, Guid blobId)
    {
        DeleteIfPresent(BlobPath(scopeId, blobId));
    }

    /// <summary>Best-effort removal of an entire scope's directory, for a deleted owner.</summary>
    public void DeleteScope(Guid scopeId)
    {
        var directory = Path.Combine(_dataDirectory.Root, _folderSegment, _leafSegment, scopeId.ToString("N"));
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The rows are already gone; an un-removable directory is an orphan for a later sweep, not a failed delete.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: never turn a successful row delete into a caller-visible failure over leftover bytes.
        }
    }

    private async Task<ManagedBlobWriteResult> VerifyExistingWriteAsync(Guid scopeId,
        Guid blobId,
        string path,
        ReadOnlyMemory<byte> content,
        string contentHash,
        CancellationToken cancellationToken)
    {
        byte[] plaintext;
        try
        {
            plaintext = Decrypt(scopeId, blobId, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is AuthenticationTagMismatchException or CryptographicException or InvalidDataException)
        {
            throw new InvalidDataException($"The existing immutable {_subject} failed verification.", exception);
        }

        if (!plaintext.AsSpan().SequenceEqual(content.Span))
        {
            throw new IOException($"The immutable {_subject} '{blobId}' already exists with different content.");
        }

        return new ManagedBlobWriteResult(OpaqueReference(scopeId, blobId), contentHash, content.Length);
    }

    private byte[] Encrypt(Guid scopeId, Guid blobId, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(Cipher.NonceSize);
        var (ciphertext, tag) = Cipher.Encrypt(_keyHolder.Key.Span, nonce, plaintext, BuildAssociatedData(scopeId, blobId));
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(payload, index: 0);
        ciphertext.CopyTo(payload, nonce.Length);
        tag.CopyTo(payload, nonce.Length + ciphertext.Length);
        return payload;
    }

    private byte[] Decrypt(Guid scopeId, Guid blobId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Cipher.NonceSize + Cipher.TagSize)
        {
            throw new InvalidDataException($"The encrypted {_subject} is too short.");
        }

        var ciphertextLength = payload.Length - Cipher.NonceSize - Cipher.TagSize;
        return Cipher.Decrypt(_keyHolder.Key.Span,
            payload[..Cipher.NonceSize],
            payload.Slice(Cipher.NonceSize, ciphertextLength),
            payload[^Cipher.TagSize..],
            BuildAssociatedData(scopeId, blobId));
    }

    private string BlobPath(Guid scopeId, Guid blobId)
    {
        return Path.Combine(_dataDirectory.Root, _folderSegment, _leafSegment, scopeId.ToString("N"), string.Concat(blobId.ToString("N"), ".blob"));
    }

    private byte[] BuildAssociatedData(Guid scopeId, Guid blobId)
    {
        return
        [
            .. scopeId.ToByteArray(bigEndian: true),
            .. blobId.ToByteArray(bigEndian: true),
            .. Encoding.UTF8.GetBytes(_aadColumn),
            .. Encoding.UTF8.GetBytes(SchemaVersion)
        ];
    }

    private static string OpaqueReference(Guid scopeId, Guid blobId) =>
        string.Concat(scopeId.ToString("N"), "/", blobId.ToString("N"));

    private static ManagedBlobReadResult Failure(ManagedBlobReadStatus status) =>
        new(status, ReadOnlyMemory<byte>.Empty);

    private static void DeleteIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The caller's write already failed, or the row is already gone. A later orphan sweep may remove the file.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original exception instead of replacing it during cleanup.
        }
    }
}
