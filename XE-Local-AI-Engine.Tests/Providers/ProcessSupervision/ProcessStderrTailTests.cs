namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Globalization;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the stderr tail sanitizes on the way in (the tail reaches a user-facing exception, so no directory may
///     survive it) and its two bounds.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ProcessStderrTailTests
{
    [Test]
    public void Append_ReducesAbsolutePathsToTheirLeaf()
    {
        var tail = new ProcessStderrTail();
        tail.Append("failed to load /home/tester/models/sd15.safetensors");

        AssertEx.Equal("failed to load sd15.safetensors", tail.Snapshot());
    }

    [Test]
    public void Snapshot_WithoutOutput_IsNull()
    {
        var tail = new ProcessStderrTail();

        AssertEx.Null(tail.Snapshot());

        tail.Append("   ");
        AssertEx.Null(tail.Snapshot());
    }

    [Test]
    public void Append_PastTheLineBound_KeepsTheLastTwenty()
    {
        var tail = new ProcessStderrTail();
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
        var tail = new ProcessStderrTail();
        tail.Append(new string('a', count: 3000));
        tail.Append(new string('b', count: 3000));

        var snapshot = AssertEx.NotNull(tail.Snapshot());

        AssertEx.Equal(expected: 3000, snapshot.Length);
        AssertEx.True(snapshot.StartsWith("bbb", StringComparison.Ordinal));
    }

    [Test]
    public void Append_OneLineOverTheCharacterBound_KeepsIt()
    {
        var tail = new ProcessStderrTail();
        tail.Append(new string('a', count: 5000));

        AssertEx.Equal(expected: 5000, AssertEx.NotNull(tail.Snapshot()).Length);
    }

    [Test]
    public void Append_WithCustomBounds_HonoursThemAndLinesIsACopy()
    {
        var tail = new ProcessStderrTail(maxLines: 3, maxCharacters: 10);
        tail.Append("aaaa");
        tail.Append("bbbb");
        tail.Append("cccc");

        var lines = tail.Lines();
        tail.Append("dd");

        AssertEx.Equal("bbbb,cccc", string.Join(',', lines), "the character bound evicted the oldest line");
        AssertEx.Equal("bbbb,cccc,dd", string.Join(',', tail.Lines()));
    }

    [Test]
    public void Constructor_RejectsANonPositiveBound()
    {
        _ = AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new ProcessStderrTail(maxLines: 0, maxCharacters: 10));
        _ = AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = new ProcessStderrTail(maxLines: 10, maxCharacters: 0));
    }
}
