namespace XE_Local_AI_Engine.Tests.Diagnostics;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Diagnostics;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="SupportBundleService" /> over a real temp log directory: the layout, the newest-first node logs, a tail
///     cut at a line boundary, absent files, scrubbing of every entry and the child-process tails.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SupportBundleServiceTests : IDisposable
{
    private readonly string _logs = Directory.CreateTempSubdirectory("xe-bundle-test-").FullName;
    private readonly INodeInfoService _nodeInfo = Substitute.For<INodeInfoService>();
    private readonly IChildProcessOutputTails _tails = Substitute.For<IChildProcessOutputTails>();

    public SupportBundleServiceTests()
    {
        _nodeInfo.GetAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new NodeInfo
        {
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            Version = "1.0.0-rc.2+abc",
            Flavour = "dev",
            SelectedChannel = "stable",
            DefaultChannel = "stable",
            IsLocalMode = false,
            IsShellOwned = false,
            OsDescription = "Linux",
            OsArchitecture = "X64",
            ProcessArchitecture = "X64",
            RuntimeFramework = ".NET",
            CpuModel = "cpu at /home/jane/cpu",
            UptimeSeconds = 1,
            Warnings = []
        });
        _tails.Snapshot().Returns([]);
    }

    public void Dispose()
    {
        Directory.Delete(_logs, recursive: true);
    }

    [Test]
    public async Task Build_WithNoLogDirectory_StillShipsManifestAndNodeInfo_AndListsEveryLogAsAbsent()
    {
        var service = CreateService(Path.Combine(_logs, "missing"));

        var entries = await ReadAsync(await service.BuildAsync(isShellOwned: false, CancellationToken.None));

        AssertEx.True(entries.ContainsKey(SupportBundleService.ManifestEntry), "manifest.json is always present.");
        AssertEx.True(entries.ContainsKey(SupportBundleService.NodeInfoEntry), "node-info.json is always present.");
        AssertEx.False(entries.Keys.Any(static name => name.Contains("snapshot", StringComparison.OrdinalIgnoreCase)), "The browser adds the snapshot, not the server.");
        using var manifest = JsonDocument.Parse(entries[SupportBundleService.ManifestEntry]);
        var statuses = manifest.RootElement.GetProperty("entries").EnumerateArray()
                               .ToDictionary(static entry => entry.GetProperty("path").GetString()!, static entry => entry.GetProperty("status").GetString());
        AssertEx.Equal(expected: 1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var log in new[] { "logs/xe-node.log", "logs/xe-node.previous.log", "logs/startup-crash.log", "logs/desktop.log", "logs/launcher.log" })
        {
            AssertEx.Equal("absent", statuses[log]);
        }
    }

    [Test]
    public async Task Build_TakesTheNewestNodeLogAndThePreviousOne_AndScrubsThem()
    {
        var older = Path.Combine(_logs, "xe-node-20261001.log");
        var newer = Path.Combine(_logs, "xe-node-20261002.log");
        await File.WriteAllTextAsync(older, "old line /home/jane/a.gguf\n");
        await File.WriteAllTextAsync(newer, "new line jane@example.com /srv/data/logs/x.log\n");
        File.SetLastWriteTimeUtc(older, DateTime.UnixEpoch.AddDays(1));
        File.SetLastWriteTimeUtc(newer, DateTime.UnixEpoch.AddDays(2));
        await File.WriteAllTextAsync(Path.Combine(_logs, "startup-crash.log"), "crash at /home/jane/x/boom.cs\n");

        var entries = await ReadAsync(await CreateService(_logs).BuildAsync(isShellOwned: false, CancellationToken.None));

        AssertEx.Equal("new line [redacted-email] <data>/logs/x.log\n", entries["logs/xe-node.log"]);
        AssertEx.Equal("old line ~/a.gguf\n", entries["logs/xe-node.previous.log"]);
        AssertEx.Equal("crash at ~/x/boom.cs\n", entries["logs/startup-crash.log"]);
        AssertEx.True(entries[SupportBundleService.NodeInfoEntry].Contains("cpu at ~/cpu", StringComparison.Ordinal), "node-info.json is scrubbed too.");
    }

    [Test]
    public async Task ReadTail_OfALargeLog_StartsAtALineBoundary_AndKeepsTheLastLine()
    {
        var path = Path.Combine(_logs, "big.log");
        var lines = Enumerable.Range(0, 5000).Select(static index => $"line {index:D5} {new string('x', 40)}").ToArray();
        await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n");

        var (text, truncated) = await SupportBundleService.ReadTailAsync(path, 4096, CancellationToken.None);

        AssertEx.True(truncated, "A file over the cap is truncated.");
        AssertEx.True(Encoding.UTF8.GetByteCount(text) <= 4096, "The tail stays within the cap.");
        var tailLines = text.TrimEnd('\n').Split('\n');
        AssertEx.True(lines.Contains(tailLines[0]), "The first kept line is a whole line: " + tailLines[0]);
        AssertEx.Equal(lines[^1], tailLines[^1]);
    }

    [Test]
    public async Task Build_WritesOneEntryPerChildProcessTail_UnderASafeFileName()
    {
        _tails.Snapshot().Returns([
            new ChildProcessOutputTail
            {
                Label = "llama-server[chat]",
                StartedUtc = DateTimeOffset.UnixEpoch,
                Lines = ["load_tensors: offloaded 29/29", "token sk-abcdefghijklmnop1234"]
            },
            new ChildProcessOutputTail
            {
                Label = "llama-server[chat]",
                StartedUtc = DateTimeOffset.UnixEpoch,
                ExitedUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
                Lines = ["exited"]
            }
        ]);

        var entries = await ReadAsync(await CreateService(_logs).BuildAsync(isShellOwned: false, CancellationToken.None));

        var first = entries["processes/llama-server-chat.txt"];
        AssertEx.True(first.Contains("load_tensors: offloaded 29/29", StringComparison.Ordinal), first);
        AssertEx.True(first.Contains("token [redacted-token]", StringComparison.Ordinal), first);
        AssertEx.True(first.Contains("exited: running", StringComparison.Ordinal), first);
        AssertEx.True(entries.ContainsKey("processes/llama-server-chat-2.txt"), "A repeated label gets a distinct entry.");
    }

    [Test]
    public async Task NodeInfoJson_StaysValidJson_WithTheVersionLiteral_AndScrubbedWarnings()
    {
        const string version = "1.0.0-rc.2+428560129c4e887f686ba07fe66f021a9afd07fc";
        _nodeInfo.GetAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new NodeInfo
        {
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            Version = version,
            Flavour = "dev",
            SelectedChannel = "stable",
            DefaultChannel = "stable",
            IsLocalMode = false,
            IsShellOwned = false,
            OsDescription = "Linux",
            OsArchitecture = "X64",
            ProcessArchitecture = "X64",
            RuntimeFramework = ".NET",
            UptimeSeconds = 1,
            Warnings = ["installed models: could not read /home/jane/models/index.json"]
        });

        var entries = await ReadAsync(await CreateService(_logs).BuildAsync(isShellOwned: false, CancellationToken.None));

        var raw = entries[SupportBundleService.NodeInfoEntry];
        AssertEx.False(raw.Contains("\\u002B", StringComparison.Ordinal), raw);
        using var document = JsonDocument.Parse(raw);
        AssertEx.Equal(version, document.RootElement.GetProperty("version").GetString());
        AssertEx.Equal("installed models: could not read ~/models/index.json", document.RootElement.GetProperty("warnings")[0].GetString());
        using var manifest = JsonDocument.Parse(entries[SupportBundleService.ManifestEntry]);
        AssertEx.Equal(version, manifest.RootElement.GetProperty("version").GetString());
    }

    private SupportBundleService CreateService(string logDirectory)
    {
        return new SupportBundleService(_nodeInfo,
            _tails,
            new SupportBundleScrubber(["/home/jane"], "/srv/data", ignoreCase: false),
            [logDirectory],
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
    }

    private static async Task<Dictionary<string, string>> ReadAsync(SupportBundle bundle)
    {
        AssertEx.True(bundle.FileName.EndsWith(".zip", StringComparison.Ordinal), bundle.FileName);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var zip = new ZipArchive(new MemoryStream(bundle.Zip.ToArray()), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            await using var stream = await entry.OpenAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            entries[entry.FullName] = await reader.ReadToEndAsync();
        }

        return entries;
    }
}
