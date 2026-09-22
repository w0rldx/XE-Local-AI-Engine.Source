namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Globalization;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the stderr tail's path sanitizer (the tail reaches a user-facing exception, so no directory may
///     survive it) and its two bounds.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ImageServerStderrTailTests
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
        AssertEx.Equal(expected, ImageServerStderrTail.Sanitize(line));
    }

    [Test]
    public void Snapshot_WithoutOutput_IsNull()
    {
        var tail = new ImageServerStderrTail();

        AssertEx.Null(tail.Snapshot());

        tail.Append("   ");
        AssertEx.Null(tail.Snapshot());
    }

    [Test]
    public void Append_PastTheLineBound_KeepsTheLastTwenty()
    {
        var tail = new ImageServerStderrTail();
        for (var index = 0; index <= 20; index++)
        {
            tail.Append(string.Create(CultureInfo.InvariantCulture, $"line{index}"));
        }

        var snapshot = AssertEx.NotNull(tail.Snapshot());

        AssertEx.Equal(expected: 20, snapshot.Split(" | ").Length);
        AssertEx.False(snapshot.Contains("line0 ", StringComparison.Ordinal));
        AssertEx.True(snapshot.StartsWith("line1 | line2", StringComparison.Ordinal));
        AssertEx.True(snapshot.EndsWith("line20", StringComparison.Ordinal));
    }

    [Test]
    public void Append_PastTheCharacterBound_KeepsTheLastLine()
    {
        var tail = new ImageServerStderrTail();
        tail.Append(new string('a', count: 3000));
        tail.Append(new string('b', count: 3000));

        var snapshot = AssertEx.NotNull(tail.Snapshot());

        AssertEx.Equal(expected: 3000, snapshot.Length);
        AssertEx.True(snapshot.StartsWith("bbb", StringComparison.Ordinal));
    }

    [Test]
    public void Append_OneLineOverTheCharacterBound_KeepsIt()
    {
        var tail = new ImageServerStderrTail();
        tail.Append(new string('a', count: 5000));

        AssertEx.Equal(expected: 5000, AssertEx.NotNull(tail.Snapshot()).Length);
    }
}
