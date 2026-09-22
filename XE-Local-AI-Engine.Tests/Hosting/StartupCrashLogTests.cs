namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
[NotInParallel]
public sealed class StartupCrashLogTests
{
    [Test]
    public async Task RecordAsync_UsesExplicitNodeDataRoot()
    {
        var root = Directory.CreateTempSubdirectory("xe-crash-root-").FullName;
        var original = Environment.GetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, root);
            await StartupCrashLog.RecordAsync("isolated");
            var text = await File.ReadAllTextAsync(Path.Combine(root, "logs", "startup-crash.log"));
            AssertEx.Contains(text, "isolated");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, original);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments(" ")]
    [Arguments("relative")]
    public async Task RecordAsync_InvalidNodeDataRoot_DoesNotFallBack(string value)
    {
        var original = Environment.GetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, value);
            AssertEx.Null(StartupCrashLog.ResolveLogDirectory());
            await StartupCrashLog.RecordAsync("must not write");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.DataDirectoryEnvironmentVariable, original);
        }
    }

    [Test]
    public async Task RecordToAsync_CreatesTheDirectoryAndAppendsTimestampedLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"xe-startup-crash-{Guid.NewGuid():N}");
        try
        {
            await StartupCrashLog.RecordToAsync(directory, "first");
            await StartupCrashLog.RecordToAsync(directory, "second");

            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "startup-crash.log"));
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
    public async Task RecordToAsync_NeverThrows_OnAnUnusablePath()
    {
        var file = Path.Combine(Path.GetTempPath(), $"xe-startup-crash-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(file, "not a directory");
        try
        {
            await StartupCrashLog.RecordToAsync(Path.Combine(file, "logs"), "ignored");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
