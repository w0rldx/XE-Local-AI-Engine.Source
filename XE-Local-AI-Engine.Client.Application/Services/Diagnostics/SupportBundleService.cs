namespace XE_Local_AI_Engine.Client.Services.Diagnostics;

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions.Diagnostics;

/// <inheritdoc />
/// <remarks>
///     Layout (schema 1): manifest, node info, tails of the newest and previous node log (256/128 KB), startup-crash,
///     desktop and launcher logs (64 KB each, cut to a line boundary) and one <c>processes/*.txt</c> per child-process
///     tail. Every text entry is scrubbed. A missing log is listed as <c>absent</c>: the Testing host and a fresh install
///     have no log directory. Bounded by construction (under 2 MB), so it is built in memory.
/// </remarks>
public sealed class SupportBundleService : ISupportBundleService
{
    public const int SchemaVersion = 1;
    public const string ManifestEntry = "manifest.json";
    public const string NodeInfoEntry = "node-info.json";

    private const int NodeLogTailBytes = 256 * 1024;
    private const int PreviousNodeLogTailBytes = 128 * 1024;
    private const int AuxiliaryLogTailBytes = 64 * 1024;
    private const string NodeLogPattern = "xe-node-*.log";

    // A file, not HTML: '+' in a version's build metadata stays literal instead of becoming \u002B.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly AuxiliaryLog[] AuxiliaryLogs =
    [
        new("logs/startup-crash.log", "startup-crash.log"),
        new("logs/desktop.log", "desktop.log"),
        new("logs/launcher.log", "launcher.log")
    ];

    private readonly IReadOnlyList<string> _logDirectories;
    private readonly INodeInfoService _nodeInfo;
    private readonly IChildProcessOutputTails _processTails;
    private readonly SupportBundleScrubber _scrubber;
    private readonly TimeProvider _timeProvider;

    /// <param name="logDirectories">Where to look for logs, in order; the first directory holding a file wins.</param>
    public SupportBundleService(INodeInfoService nodeInfo,
        IChildProcessOutputTails processTails,
        SupportBundleScrubber scrubber,
        IReadOnlyList<string> logDirectories,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(nodeInfo);
        ArgumentNullException.ThrowIfNull(processTails);
        ArgumentNullException.ThrowIfNull(scrubber);
        ArgumentNullException.ThrowIfNull(logDirectories);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _nodeInfo = nodeInfo;
        _processTails = processTails;
        _scrubber = scrubber;
        _logDirectories = logDirectories;
        _timeProvider = timeProvider;
    }

    public async Task<SupportBundle> BuildAsync(bool isShellOwned, CancellationToken ct)
    {
        var createdAt = _timeProvider.GetUtcNow();
        var nodeInfo = await _nodeInfo.GetAsync(isShellOwned, ct);
        var entries = new List<ManifestEntryInfo>();

        using var buffer = new MemoryStream();
        await using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Structured entries are scrubbed per value, never as serialized text, which a text rule could make invalid JSON.
            await WriteEntryAsync(zip, NodeInfoEntry, JsonSerializer.Serialize(ScrubFreeText(nodeInfo), JsonOptions), ct);
            entries.Add(new ManifestEntryInfo
            {
                Path = NodeInfoEntry,
                Status = "included"
            });

            var nodeLogs = FindNodeLogs();
            await AddLogAsync(zip, entries, "logs/xe-node.log", nodeLogs.ElementAtOrDefault(0), NodeLogTailBytes, ct);
            await AddLogAsync(zip, entries, "logs/xe-node.previous.log", nodeLogs.ElementAtOrDefault(1), PreviousNodeLogTailBytes, ct);
            foreach (var (entry, fileName) in AuxiliaryLogs)
            {
                await AddLogAsync(zip, entries, entry, FindFile(fileName), AuxiliaryLogTailBytes, ct);
            }

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tail in _processTails.Snapshot())
            {
                var entry = "processes/" + UniqueFileName(tail.Label, usedNames) + ".txt";
                await AddTextAsync(zip, entries, entry, RenderTail(tail), truncated: false, ct);
            }

            var manifest = new
            {
                SchemaVersion,
                CreatedAtUtc = createdAt,
                nodeInfo.Version,
                Entries = entries
            };
            await WriteEntryAsync(zip, ManifestEntry, JsonSerializer.Serialize(manifest, JsonOptions), ct);
        }

        return new SupportBundle
        {
            Zip = buffer.ToArray(),
            FileName = $"xe-support-{createdAt:yyyyMMdd-HHmmss}.zip"
        };
    }

    /// <summary>The last <paramref name="maxBytes" /> of a file, cut forward to the first line break when truncated.</summary>
    public static async Task<LogTail> ReadTailAsync(string path, int maxBytes, CancellationToken ct)
    {
        // The node log's active file is held open by the rolling sink, hence the sharing flags.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        var truncated = stream.Length > maxBytes;
        if (truncated)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
        }

        var bytes = new byte[Math.Min(stream.Length, maxBytes)];
        await stream.ReadExactlyAsync(bytes, ct);
        var start = 0;
        if (truncated)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n');
            start = newline >= 0 ? newline + 1 : 0;
        }

        return new LogTail(Encoding.UTF8.GetString(bytes, start, bytes.Length - start), truncated);
    }

    /// <summary>A file tail read by <see cref="ReadTailAsync" />: the text and whether the start was cut.</summary>
    public readonly record struct LogTail(string Text, bool Truncated);

    private readonly record struct AuxiliaryLog(string Entry, string FileName);

    // The free-text members: warnings, the CPU model string and settings values (model names, voice profile). The
    // rest are versions, enums, numbers and fixed vocabularies.
    private NodeInfo ScrubFreeText(NodeInfo info)
    {
        return info with
        {
            CpuModel = info.CpuModel is null ? null : _scrubber.Scrub(info.CpuModel),
            Warnings = info.Warnings.Select(_scrubber.Scrub).ToArray(),
            Settings = info.Settings?.ToDictionary(static pair => pair.Key, pair => pair.Value is null ? null : _scrubber.Scrub(pair.Value), StringComparer.Ordinal)
        };
    }

    private static string RenderTail(ChildProcessOutputTail tail)
    {
        var builder = new StringBuilder();
        builder.Append("label: ").Append(tail.Label).Append('\n');
        builder.Append("started: ").Append(tail.StartedUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("exited: ")
               .Append(tail.ExitedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "running")
               .Append("\n\n");
        foreach (var line in tail.Lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    private static string UniqueFileName(string label, HashSet<string> used)
    {
        var safe = new string(label.Select(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray()).Trim('-');
        if (safe.Length == 0)
        {
            safe = "process";
        }

        var candidate = safe;
        var index = 1;
        while (!used.Add(candidate))
        {
            index++;
            candidate = $"{safe}-{index}";
        }

        return candidate;
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(ct);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    // Newest first, from the first configured directory that holds any node log.
    private IReadOnlyList<string> FindNodeLogs()
    {
        foreach (var directory in _logDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var logs = new DirectoryInfo(directory).GetFiles(NodeLogPattern)
                                                   .OrderByDescending(static file => file.LastWriteTimeUtc)
                                                   .ThenByDescending(static file => file.Name, StringComparer.Ordinal)
                                                   .Select(static file => file.FullName)
                                                   .ToArray();
            if (logs.Length > 0)
            {
                return logs;
            }
        }

        return [];
    }

    private string? FindFile(string fileName)
    {
        return _logDirectories.Select(directory => Path.Combine(directory, fileName)).FirstOrDefault(File.Exists);
    }

    private async Task AddLogAsync(ZipArchive zip, List<ManifestEntryInfo> entries, string entry, string? path, int maxBytes, CancellationToken ct)
    {
        if (path is null)
        {
            entries.Add(new ManifestEntryInfo
            {
                Path = entry,
                Status = "absent"
            });
            return;
        }

        string text;
        bool truncated;
        try
        {
            (text, truncated) = await ReadTailAsync(path, maxBytes, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            entries.Add(new ManifestEntryInfo
            {
                Path = entry,
                Status = "unreadable"
            });
            return;
        }

        await AddTextAsync(zip, entries, entry, text, truncated, ct);
    }

    private async Task AddTextAsync(ZipArchive zip, List<ManifestEntryInfo> entries, string entry, string text, bool truncated, CancellationToken ct)
    {
        await WriteEntryAsync(zip, entry, _scrubber.Scrub(text), ct);
        entries.Add(new ManifestEntryInfo
        {
            Path = entry,
            Status = "included",
            Truncated = truncated
        });
    }

    private sealed record ManifestEntryInfo
    {
        public required string Path { get; init; }

        public required string Status { get; init; }

        public bool Truncated { get; init; }
    }
}
