namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class StartupCrashLogTests
{
    [Test]
    public void RecordTo_CreatesTheDirectoryAndAppendsTimestampedLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xe-startup-crash-{Guid.NewGuid():N}");
        try
        {
            StartupCrashLog.RecordTo(directory, "first");
            StartupCrashLog.RecordTo(directory, "second");

            var lines = File.ReadAllLines(Path.Combine(directory, "startup-crash.log"));
            AssertEx.Equal(expected: 2, lines.Length);
            AssertEx.Contains(lines[0], "first");
            AssertEx.Contains(lines[1], "second");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void RecordTo_NeverThrows_OnAnUnusablePath()
    {
        var file = Path.Combine(Path.GetTempPath(), $"xe-startup-crash-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(file, "not a directory");
        try
        {
            StartupCrashLog.RecordTo(Path.Combine(file, "logs"), "ignored");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
