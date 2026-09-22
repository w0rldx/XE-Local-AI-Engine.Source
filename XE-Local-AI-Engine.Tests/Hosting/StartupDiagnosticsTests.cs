namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.WindowsLauncher;

[Category(TestCategories.Unit)]
[NotInParallel]
public sealed class StartupDiagnosticsTests
{
    [Test]
    public void ResolveLogDirectory_UnsetKeepsDefault()
    {
        AssertEx.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine", "logs"), StartupDiagnostics.ResolveLogDirectory(null));
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("relative")]
    [Arguments("/tmp/path\n")]
    public void ResolveLogDirectory_InvalidOverrideDoesNotFallBack(string value)
    {
        AssertEx.Null(StartupDiagnostics.ResolveLogDirectory(value));
    }

    [Test]
    public async Task Record_UsesExplicitNodeDataRoot()
    {
        var root = Directory.CreateTempSubdirectory("xe-launcher-root-").FullName;
        var original = Environment.GetEnvironmentVariable("XE_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XE_DATA_DIR", root);
            StartupDiagnostics.Record("isolated");
            var text = await File.ReadAllTextAsync(Path.Combine(root, "logs", "launcher.log"));
            AssertEx.Contains(text, "isolated");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XE_DATA_DIR", original);
            Directory.Delete(root, recursive: true);
        }
    }

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
