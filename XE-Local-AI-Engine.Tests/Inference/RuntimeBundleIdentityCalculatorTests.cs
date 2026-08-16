namespace XE_Local_AI_Engine.Tests.Inference;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the runtime-bundle identity byte stream. The hash is a member of every persisted launch-policy
///     fingerprint, so a change here silently invalidates every frozen inference profile — the literal below is an
///     independent restatement of the documented framing (int32-LE name length, UTF-8 name, int64-LE file length, content hash),
///     over the bundle files in ordinal name order.
/// </summary>
public sealed class RuntimeBundleIdentityCalculatorTests
{
    private const string ExecutableFileName = "llama-server";

    private const string ExpectedIdentity = "c18c7a2ccf3483c7c8bb26e18e631a62d8fc2d4556c62bed414983114207ae49";

    [Test]
    public async Task ComputeAsync_PinnedBundle_MatchesTheDocumentedHash()
    {
        var directory = CreateBundle();
        try
        {
            var bundle = await RuntimeBundleIdentityCalculator.ComputeAsync(Path.Combine(directory, ExecutableFileName),
                Sha256Async,
                CancellationToken.None);

            AssertEx.Equal(ExpectedIdentity, bundle.Identity);
            AssertEx.Equal(expected: 2, bundle.FileCount);
            AssertEx.Equal("libggml.so", bundle.Files[0].Name, "files are hashed and listed in ordinal name order");
            AssertEx.Equal(ExecutableFileName, bundle.Files[1].Name);
            AssertEx.Equal(expected: 15L, bundle.Files[0].SizeBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ComputeAsync_IgnoresNonRuntimeSiblingsAndFailsOnAnEmptyBundle()
    {
        var directory = CreateBundle();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "not part of the runtime");

            var bundle = await RuntimeBundleIdentityCalculator.ComputeAsync(Path.Combine(directory, ExecutableFileName),
                Sha256Async,
                CancellationToken.None);

            AssertEx.Equal(ExpectedIdentity, bundle.Identity, "an unrelated sibling file must not move the identity");

            var emptyDirectory = Path.Combine(directory, "empty");
            Directory.CreateDirectory(emptyDirectory);
            _ = await AssertEx.ThrowsAsync<FileNotFoundException>(() =>
                RuntimeBundleIdentityCalculator.ComputeAsync(Path.Combine(emptyDirectory, ExecutableFileName),
                    Sha256Async,
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct) =>
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));

    private static string CreateBundle()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ExecutableFileName), "binary-revision-1");
        File.WriteAllText(Path.Combine(directory, "libggml.so"), "ggml-revision-1");
        return directory;
    }
}
