namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class ManagedDevelopmentArtifactBlobStore : IDevelopmentArtifactBlobStore
{
    private const string DevelopmentFolder = "development";
    private const string ArtifactsFolder = "artifacts";
    private const string BlobColumn = "development_artifact_blob";
    private const string SchemaVersion = "v1";

    private static readonly INodeAeadCipher Cipher = new AesGcmNodeAeadCipher();

    private readonly INodeDataDirectory _dataDirectory;
    private readonly INodeSqliteKeyHolder _keyHolder;
    private readonly DevelopmentOptions _options;

    public ManagedDevelopmentArtifactBlobStore(INodeDataDirectory dataDirectory,
        INodeSqliteKeyHolder keyHolder,
        IOptions<DevelopmentOptions> options)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _keyHolder = keyHolder ?? throw new ArgumentNullException(nameof(keyHolder));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<DevelopmentArtifactBlobWriteResult> WriteAsync(Guid projectId,
        Guid artifactId,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        if (content.Length > _options.MaxArtifactBytes)
        {
            throw new InvalidOperationException($"The Development artifact exceeds the configured {_options.MaxArtifactBytes}-byte limit.");
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(content.Span));
        var finalPath = ArtifactPath(projectId, artifactId);
        var directory = Path.GetDirectoryName(finalPath) ?? throw new InvalidOperationException("The managed Development artifact directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var tempPath = string.Concat(finalPath, ".", Guid.NewGuid().ToString("N"), ".tmp");

        if (File.Exists(finalPath))
        {
            throw new IOException($"The immutable Development artifact '{artifactId}' already exists.");
        }

        try
        {
            var encrypted = Encrypt(projectId, artifactId, content.Span);
            await File.WriteAllBytesAsync(tempPath, encrypted, cancellationToken).ConfigureAwait(false);

            var verifiedEncrypted = await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
            var verifiedPlaintext = Decrypt(projectId, artifactId, verifiedEncrypted);
            var verifiedHash = Convert.ToHexString(SHA256.HashData(verifiedPlaintext));
            if (verifiedPlaintext.LongLength != content.Length || !string.Equals(verifiedHash, contentHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The temporary Development artifact failed hash/size verification.");
            }

            File.Move(tempPath, finalPath, overwrite: false);
            return new DevelopmentArtifactBlobWriteResult(OpaqueReference(projectId, artifactId), contentHash, content.Length);
        }
        catch
        {
            DeleteIfPresent(tempPath);
            throw;
        }
    }

    public async Task<DevelopmentArtifactBlobReadResult> ReadAsync(Guid projectId,
        Guid artifactId,
        string expectedHash,
        long expectedByteCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        var path = ArtifactPath(projectId, artifactId);
        if (!File.Exists(path))
        {
            return DevelopmentArtifactBlobReadResult.Failure(DevelopmentArtifactReadStatus.Missing);
        }

        byte[] plaintext;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            plaintext = Decrypt(projectId, artifactId, encrypted);
        }
        catch (Exception exception) when (exception is AuthenticationTagMismatchException or CryptographicException or InvalidDataException)
        {
            return DevelopmentArtifactBlobReadResult.Failure(DevelopmentArtifactReadStatus.Tampered);
        }

        if (plaintext.LongLength != expectedByteCount)
        {
            return DevelopmentArtifactBlobReadResult.Failure(DevelopmentArtifactReadStatus.SizeMismatch);
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(plaintext));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return DevelopmentArtifactBlobReadResult.Failure(DevelopmentArtifactReadStatus.HashMismatch);
        }

        return new DevelopmentArtifactBlobReadResult(DevelopmentArtifactReadStatus.Found, plaintext);
    }

    private byte[] Encrypt(Guid projectId, Guid artifactId, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(Cipher.NonceSize);
        var (ciphertext, tag) = Cipher.Encrypt(_keyHolder.Key.Span, nonce, plaintext, BuildAssociatedData(projectId, artifactId));
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(payload, index: 0);
        ciphertext.CopyTo(payload, nonce.Length);
        tag.CopyTo(payload, nonce.Length + ciphertext.Length);
        return payload;
    }

    private byte[] Decrypt(Guid projectId, Guid artifactId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Cipher.NonceSize + Cipher.TagSize)
        {
            throw new InvalidDataException("The encrypted Development artifact is too short.");
        }

        var ciphertextLength = payload.Length - Cipher.NonceSize - Cipher.TagSize;
        return Cipher.Decrypt(_keyHolder.Key.Span,
            payload[..Cipher.NonceSize],
            payload.Slice(Cipher.NonceSize, ciphertextLength),
            payload[^Cipher.TagSize..],
            BuildAssociatedData(projectId, artifactId));
    }

    private string ArtifactPath(Guid projectId, Guid artifactId)
    {
        return Path.Combine(_dataDirectory.Root,
            DevelopmentFolder,
            ArtifactsFolder,
            projectId.ToString("N"),
            string.Concat(artifactId.ToString("N"), ".blob"));
    }

    private static string OpaqueReference(Guid projectId, Guid artifactId)
        => string.Concat(projectId.ToString("N"), "/", artifactId.ToString("N"));

    private static byte[] BuildAssociatedData(Guid projectId, Guid artifactId)
    {
        return [.. projectId.ToByteArray(bigEndian: true),
                .. artifactId.ToByteArray(bigEndian: true),
                .. Encoding.UTF8.GetBytes(BlobColumn),
                .. Encoding.UTF8.GetBytes(SchemaVersion)];
    }

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
            // The write already failed. A later orphan sweep may remove an un-deletable temp file.
        }
        catch (UnauthorizedAccessException)
        {
            // The write already failed. Preserve the original exception instead of replacing it during cleanup.
        }
    }
}
