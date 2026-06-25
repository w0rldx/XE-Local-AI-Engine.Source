namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards for Hugging-Face-supplied file names (untrusted input). Ensures traversal/rooted names are rejected and a
///     contained name resolves under the models directory (the defense-in-depth used by discovery + the store).
/// </summary>
public sealed class GgufFilePathTests
{
    [Test]
    [Arguments("Demo-Model-Q4_K_M.gguf", true)]
    [Arguments("subdir/Demo-Q4_K_M.gguf", true)]
    [Arguments("../escape-Q4_K_M.gguf", false)]
    [Arguments("../../../../etc/evil-Q4_K_M.gguf", false)]
    [Arguments("dir/../../escape.gguf", false)]
    [Arguments("./relative-Q4_K_M.gguf", false)]
    [Arguments("", false)]
    public void IsSafeRelativePath_ClassifiesNamesByTraversalSafety(string fileName, bool expected)
    {
        AssertEx.Equal(expected, GgufFilePath.IsSafeRelativePath(fileName));
    }

    [Test]
    public async Task ResolveContainedPath_KeepsContainedNameUnderBase()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "xe-hf-contain-" + Guid.NewGuid().ToString("N"));

        var resolved = GgufFilePath.ResolveContainedPath(baseDir, "Demo-Q4_K_M.gguf");

        AssertEx.True(resolved.StartsWith(Path.GetFullPath(baseDir), StringComparison.Ordinal));
        AssertEx.Equal("Demo-Q4_K_M.gguf", Path.GetFileName(resolved));
    }

    [Test]
    public void ResolveContainedPath_ThrowsOnTraversalEscape()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "xe-hf-contain-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentException>(() =>
            GgufFilePath.ResolveContainedPath(baseDir, "../../../../tmp/evil-Q4_K_M.gguf"));
    }
}
