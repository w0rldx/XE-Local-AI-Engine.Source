namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The shared file hash: lowercase hex SHA-256, and the length read from the same stream.</summary>
[Category(TestCategories.Unit)]
public sealed class FileSha256Tests
{
    [Test]
    public async Task ComputeWithLengthAsync_ReturnsTheLowercaseHexDigestAndTheByteLength()
    {
        using var temp = new TempDirectory("xe-file-sha256");
        var path = temp.FilePath("abc.bin");
        await File.WriteAllTextAsync(path, "abc");

        var digest = await FileSha256.ComputeWithLengthAsync(path, CancellationToken.None);

        AssertEx.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", digest.Sha256);
        AssertEx.Equal(expected: 3L, digest.SizeBytes);
        AssertEx.Equal(digest.Sha256, await FileSha256.ComputeAsync(path, CancellationToken.None));
    }
}
