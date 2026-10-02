namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
            var unusable = Path.Combine(file, "logs");

            await AssertEx.CompletesAsync(StartupCrashLog.RecordToAsync(unusable, "ignored"),
                TestBudgets.Contended,
                "a crash log that cannot be written must not add a second failure to the one being reported.");

            AssertEx.False(Directory.Exists(unusable), "nothing was created under a path whose parent is a file.");
            AssertEx.Equal("not a directory", await File.ReadAllTextAsync(file), "the file standing in the way is left untouched.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task RecordToAsync_OversizedFile_KeepsOnlyTheNewestTailFromALineStart()
    {
        var directory = Directory.CreateTempSubdirectory("xe-crash-cap-").FullName;
        var path = Path.Combine(directory, "startup-crash.log");
        try
        {
            // ~1.5 MB of numbered 100-byte lines, so a cut that lands mid-line is visible.
            var builder = new StringBuilder();
            var lineNumber = 0;
            while (builder.Length < 1536 * 1024)
            {
                builder.Append((lineNumber++).ToString("D8", CultureInfo.InvariantCulture)).Append(':').Append('x', 90).Append('\n');
            }

            await File.WriteAllTextAsync(path, builder.ToString());

            await StartupCrashLog.RecordToAsync(directory, "newest entry");

            var text = await File.ReadAllTextAsync(path);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            AssertEx.True(new FileInfo(path).Length <= StartupCrashLog.KeepBytes + 200,
                $"the capped file must hold at most the kept tail plus the new entry, was {new FileInfo(path).Length} bytes.");
            AssertEx.True(Regex.IsMatch(lines[0], "^[0-9]{8}:x{90}$", RegexOptions.None, TimeSpan.FromSeconds(1)),
                $"the kept tail must start at a line boundary, began with \"{lines[0][..Math.Min(20, lines[0].Length)]}\".");
            AssertEx.Contains(lines[^1], "newest entry");
            AssertEx.Contains(text, builder.ToString()[^1000..], message: "the newest original lines survive.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task RecordToAsync_SmallFile_IsAppendedNotTrimmed()
    {
        var directory = Directory.CreateTempSubdirectory("xe-crash-cap-").FullName;
        var path = Path.Combine(directory, "startup-crash.log");
        try
        {
            const string existing = "old line\n";
            await File.WriteAllTextAsync(path, existing);

            await StartupCrashLog.RecordToAsync(directory, "new entry");

            var text = await File.ReadAllTextAsync(path);
            AssertEx.True(text.StartsWith(existing, StringComparison.Ordinal), "a file under the cap keeps every byte.");
            AssertEx.Contains(text, "new entry");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
