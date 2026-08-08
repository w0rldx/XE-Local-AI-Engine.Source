namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.WindowsLauncher;

public sealed class StartupDiagnosticsTests
{
    [Test]
    public void RecordTo_CreatesTheDirectoryAndAppendsTimestampedLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xe-launcher-diag-{Guid.NewGuid():N}");
        try
        {
            StartupDiagnostics.RecordTo(directory, "first");
            StartupDiagnostics.RecordTo(directory, "second");

            var lines = File.ReadAllLines(Path.Combine(directory, "launcher.log"));
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
        // A path that cannot be created (a file standing where a directory segment must be) must be swallowed: a
        // diagnostics failure must never become a second failure on top of the startup failure it records.
        var file = Path.Combine(Path.GetTempPath(), $"xe-launcher-diag-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(file, "not a directory");
        try
        {
            StartupDiagnostics.RecordTo(Path.Combine(file, "logs"), "ignored");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
