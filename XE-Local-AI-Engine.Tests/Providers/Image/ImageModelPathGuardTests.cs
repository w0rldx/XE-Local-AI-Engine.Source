namespace XE_Local_AI_Engine.Tests.Providers.Image;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The image-model store reuses the shared <see cref="GgufFilePath" /> containment guard for every downloaded weight
///     part (a repo is untrusted input). This freezes that the guard rejects traversal for image-style part names.
/// </summary>
public sealed class ImageModelPathGuardTests
{
    [Test]
    [Arguments("flux1-schnell.gguf", true)]
    [Arguments("subdir/ae.safetensors", true)]
    [Arguments("../escape.safetensors", false)]
    [Arguments("../../../../etc/evil.gguf", false)]
    [Arguments("dir/../../escape.gguf", false)]
    [Arguments("./relative.gguf", false)]
    [Arguments("", false)]
    public void GgufFilePathGuard_RejectsTraversal(string fileName, bool expected)
    {
        AssertEx.Equal(expected, GgufFilePath.IsSafeRelativePath(fileName));
    }

    [Test]
    public void ResolveContainedPath_ThrowsOnTraversalEscape_ForImagePart()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "xe-image-contain-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentException>(() =>
            GgufFilePath.ResolveContainedPath(baseDir, "../../../../tmp/evil.safetensors"));
    }
}
