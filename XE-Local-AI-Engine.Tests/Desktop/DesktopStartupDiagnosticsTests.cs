namespace XE_Local_AI_Engine.Tests.Desktop;

using System.Globalization;
using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class DesktopStartupDiagnosticsTests
{
    [Test]
    public async Task RecordTo_AppendsOneTimestampedLinePerFailure()
    {
        using var directory = new TempDirectory();
        var logs = Path.Combine(directory.Path, "logs");
        DesktopStartupDiagnostics.RecordTo(logs, "first failure");
        DesktopStartupDiagnostics.RecordTo(logs, "second failure");

        var lines = await File.ReadAllLinesAsync(Path.Combine(logs, "desktop.log"));
        AssertEx.Equal(2, lines.Length);
        AssertEx.Contains(lines[0], "first failure");
        AssertEx.Contains(lines[1], "second failure");
        foreach (var line in lines)
        {
            var stamp = line[1..line.IndexOf(']', StringComparison.Ordinal)];
            AssertEx.True(DateTimeOffset.TryParseExact(stamp, "yyyy-MM-dd HH:mm:ss.fff zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _), line);
        }
    }

    [Test]
    public void ResolveLogDirectory_DefaultsToThePerUserRootTheHostAlreadyLogsInto()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DesktopStartupOptions.ApplicationDataFolderName, "logs");
        AssertEx.Equal(expected, DesktopStartupDiagnostics.ResolveLogDirectory(null));
    }

    [Test]
    public void ResolveLogDirectory_HonorsAnAbsoluteOverrideAndRejectsUnusableOnes()
    {
        using var directory = new TempDirectory();
        AssertEx.Equal(Path.Combine(directory.Path, "logs"), DesktopStartupDiagnostics.ResolveLogDirectory(directory.Path));
        foreach (var unusable in new[]
                 {
                     string.Empty,
                     "   ",
                     "relative/path",
                     directory.Path + "\n"
                 })
        {
            AssertEx.Null(DesktopStartupDiagnostics.ResolveLogDirectory(unusable), unusable);
        }
    }

    [Test]
    public async Task RecordTo_SwallowsAWriteFailureSoItNeverMasksTheFailureItRecords()
    {
        using var directory = new TempDirectory();
        // A file where the logs directory should be: Directory.CreateDirectory cannot win, on any platform.
        var blocked = Path.Combine(directory.Path, "logs");
        await File.WriteAllTextAsync(blocked, "not a directory");

        AssertEx.DoesNotThrow(() => DesktopStartupDiagnostics.RecordTo(blocked, "unrecorded failure"),
            "A diagnostics failure must not become a second failure on top of the one it records.");
        AssertEx.Equal("not a directory", await File.ReadAllTextAsync(blocked));
    }
}
