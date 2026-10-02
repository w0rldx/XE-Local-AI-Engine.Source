namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared absolute-path sanitizer behind the child-process tails and the support-bundle scrubber: only the
///     leaf of a drive-rooted, UNC or POSIX path survives, URLs and prose stay.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AbsolutePathSanitizerTests
{
    [Test]
    [Arguments(@"failed to load C:\Users\tester\models\sd15.safetensors", "failed to load sd15.safetensors")]
    [Arguments(@"failed to load C:\Users\Jane Doe\models\sd15.safetensors", "failed to load sd15.safetensors")]
    [Arguments(@"load \\fileserver\share\Model Files\sd15.safetensors now", "load sd15.safetensors now")]
    [Arguments(@"open ""C:\Users\Jane Doe\models\sd15.safetensors"" failed", @"open ""sd15.safetensors"" failed")]
    [Arguments("open '/home/tester/models/sd15.safetensors' failed", "open 'sd15.safetensors' failed")]
    [Arguments("failed to load /home/tester/models/sd15.safetensors", "failed to load sd15.safetensors")]
    [Arguments("/home/Jane Doe/models/private/sd.safetensors", "sd.safetensors")]
    [Arguments("failed to load /home/Jane Doe/models/sd.safetensors failed to open",
        "failed to load sd.safetensors failed to open")]
    [Arguments("/home/a/b.txt and then /home/c/d.txt", "b.txt and then d.txt")]
    [Arguments("GET https://huggingface.co/org/repo/resolve/main/sd.safetensors 404",
        "GET https://huggingface.co/org/repo/resolve/main/sd.safetensors 404")]
    [Arguments(@"copy C:\a\b.txt and C:\d\e.txt", "copy b.txt and e.txt")]
    [Arguments(@"copy C:\a\b.txt failed /home/x/y.txt", "copy b.txt failed y.txt")]
    [Arguments(@"C:\Users\Jane Doe\models\sd15.safetensors failed to open", "sd15.safetensors failed to open")]
    [Arguments("download https://huggingface.co/org/repo/resolve/main/sd15.safetensors failed",
        "download https://huggingface.co/org/repo/resolve/main/sd15.safetensors failed")]
    [Arguments("ggml_cuda_init: no CUDA devices found", "ggml_cuda_init: no CUDA devices found")]
    public void Sanitize_KeepsOnlyTheLeafAndTheProse(string line, string expected)
    {
        AssertEx.Equal(expected, AbsolutePathSanitizer.Sanitize(line));
    }

    [Test]
    public void Sanitize_IsIdempotent()
    {
        var once = AbsolutePathSanitizer.Sanitize(@"copy C:\a\b.txt failed /home/x/y.txt");

        AssertEx.Equal(once, AbsolutePathSanitizer.Sanitize(once));
    }
}
