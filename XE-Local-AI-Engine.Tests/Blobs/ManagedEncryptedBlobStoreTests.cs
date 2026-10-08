namespace XE_Local_AI_Engine.Tests.Blobs;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The read-failure statuses of the managed blob store, produced against real files on disk rather than faked at
///     the wrapper level: truncation, a flipped bit and a blob moved to another id must each read as tampered.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ManagedEncryptedBlobStoreTests : IDisposable
{
    private static readonly byte[] Content = "the managed blob content"u8.ToArray();

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly TempDirectory _root = new("xe-managed-blob-tests");
    private readonly ManagedEncryptedBlobStore _store;

    public ManagedEncryptedBlobStoreTests()
    {
        _store = new ManagedEncryptedBlobStore(new FakeNodeDataDirectory(_root.Path),
            _keyHolder,
            "folder",
            "leaf",
            "column",
            maxBytes: 1024,
            subject: "test blob");
    }

    public void Dispose()
    {
        _root.Dispose();
        _keyHolder.Dispose();
    }

    [Test]
    public async Task Read_AfterWrite_IsFound()
    {
        var (scopeId, blobId, hash) = await WriteAsync();

        var result = await _store.ReadAsync(scopeId, blobId, hash, Content.Length);

        AssertEx.Equal(ManagedBlobReadStatus.Found, result.Status);
        AssertEx.True(result.Content.Span.SequenceEqual(Content));
    }

    [Test]
    public async Task Read_WhenTheFileIsTruncated_IsTamperedWithNoContent()
    {
        var (scopeId, blobId, hash) = await WriteAsync();
        await using (var file = new FileStream(BlobPath(scopeId, blobId), FileMode.Open, FileAccess.Write))
        {
            file.SetLength(10);
        }

        var result = await _store.ReadAsync(scopeId, blobId, hash, Content.Length);

        AssertEx.Equal(ManagedBlobReadStatus.Tampered, result.Status);
        AssertEx.True(result.Content.IsEmpty);
    }

    [Test]
    public async Task Read_WhenOneCiphertextBitIsFlipped_IsTampered()
    {
        var (scopeId, blobId, hash) = await WriteAsync();
        var path = BlobPath(scopeId, blobId);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes);

        var result = await _store.ReadAsync(scopeId, blobId, hash, Content.Length);

        AssertEx.Equal(ManagedBlobReadStatus.Tampered, result.Status);
    }

    [Test]
    public async Task Read_WhenABlobIsSwappedAcrossScopes_IsTampered()
    {
        // The AAD binds scope and blob id, so a valid ciphertext moved to another id must not decrypt there.
        var (scopeId, blobId, hash) = await WriteAsync();
        var otherScopeId = Guid.NewGuid();
        var otherPath = BlobPath(otherScopeId, blobId);
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.Copy(BlobPath(scopeId, blobId), otherPath);

        var result = await _store.ReadAsync(otherScopeId, blobId, hash, Content.Length);

        AssertEx.Equal(ManagedBlobReadStatus.Tampered, result.Status);
    }

    [Test]
    public async Task Read_WithTheWrongExpectations_ReportsSizeThenHashMismatch()
    {
        var (scopeId, blobId, hash) = await WriteAsync();

        var wrongSize = await _store.ReadAsync(scopeId, blobId, hash, Content.Length + 1);
        var wrongHash = await _store.ReadAsync(scopeId, blobId, new string('0', hash.Length), Content.Length);

        AssertEx.Equal(ManagedBlobReadStatus.SizeMismatch, wrongSize.Status);
        AssertEx.Equal(ManagedBlobReadStatus.HashMismatch, wrongHash.Status);
    }

    [Test]
    public async Task Write_WithDifferentContentToTheSameIds_ThrowsAndKeepsTheOriginal()
    {
        var (scopeId, blobId, hash) = await WriteAsync();

        await AssertEx.ThrowsAsync<IOException>(() => _store.WriteAsync(scopeId, blobId, "different content"u8.ToArray()));

        var result = await _store.ReadAsync(scopeId, blobId, hash, Content.Length);
        AssertEx.Equal(ManagedBlobReadStatus.Found, result.Status);
        AssertEx.True(result.Content.Span.SequenceEqual(Content));
        AssertEx.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(BlobPath(scopeId, blobId))!, "*.tmp"));
    }

    private async Task<WrittenBlob> WriteAsync()
    {
        var scopeId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var written = await _store.WriteAsync(scopeId, blobId, Content);
        AssertEx.Equal(Convert.ToHexString(SHA256.HashData(Content)), written.ContentHash);
        return new WrittenBlob(scopeId, blobId, written.ContentHash);
    }

    private readonly record struct WrittenBlob(Guid ScopeId, Guid BlobId, string Hash);

    private string BlobPath(Guid scopeId, Guid blobId) =>
        Path.Combine(_root.Path, "folder", "leaf", scopeId.ToString("N"), string.Concat(blobId.ToString("N"), ".blob"));
}
