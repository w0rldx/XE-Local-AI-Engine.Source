namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Run list <see cref="IAgentHomeRunListService" />, built from a bounded scan of the runs directory.
/// </summary>
/// <remarks>
///     Ordering and paging need no file at all: the directory name carries the instant the node minted it. Every
///     other field comes from a CAPPED read of that run's own files, because a run's logs are partly model-influenced
///     and can be huge or malformed — so nothing is read whole, no JSON shape is trusted, and a value that cannot be
///     read defensively is reported as unknown instead of failing the page. Only closed tokens and node-minted ids
///     leave this service: a log's free-text detail never does.
/// </remarks>
internal sealed class AgentHomeRunListService : IAgentHomeRunListService
{
    /// <summary>Bytes read from the head of <c>events.jsonl</c>, where the <c>started</c> event sits.</summary>
    private const int EventsHeadBytes = 8192;

    /// <summary>Bytes read from the tail of <c>events.jsonl</c>, where the outcome and apply events sit.</summary>
    private const int EventsTailBytes = 262144;

    /// <summary>Size beyond which <c>changed-files.json</c> is reported as uncounted rather than parsed.</summary>
    private const int ChangedFilesMaxBytes = 4194304;

    /// <summary>
    ///     No whole-file cap for <c>events.jsonl</c>: the head and tail windows are what bound that read, and a run
    ///     with a huge log is exactly the one whose outcome is still worth reporting.
    /// </summary>
    private const long EventsMaxBytes = long.MaxValue;

    /// <summary>Ceiling on entries one run's size walk visits.</summary>
    private const int MaxMeasuredEntriesPerRun = 20000;

    /// <summary>Longest line the reader will hand to the JSON parser; a longer one is skipped, not truncated.</summary>
    private const int MaxLogLineChars = 65536;

    /// <summary>
    ///     Bytes of <c>changes.patch</c> a viewer is handed, independent of the apply budget.
    /// </summary>
    /// <remarks>
    ///     A display cap, not a policy one: the apply budget is a runtime setting about what may be LANDED, and a
    ///     patch well inside it is still more than a browser tab should be asked to render. Nothing is refused for
    ///     being over this — the read reports <c>truncated</c> and the operator still sees the beginning.
    /// </remarks>
    private const int PatchDisplayMaxBytes = 1048576;

    private readonly string _dataDirectoryRoot;
    private readonly AgentHomeOptions _options;

    public AgentHomeRunListService(IOptions<AgentHomeOptions> options, INodeDataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        _options = options.Value;
        _dataDirectoryRoot = dataDirectory.Root;
    }

    /// <inheritdoc />
    public async Task<AgentHomeRunPage> ListAsync(int limit, int offset, CancellationToken cancellationToken = default)
    {
        var runsRoot = AgentHomeRunPaths.ResolveRunsRoot(_options, _dataDirectoryRoot);
        if (!Directory.Exists(runsRoot))
        {
            return new AgentHomeRunPage { Items = [], TotalCount = 0 };
        }

        var runs = new List<AgentHomeRunLocation>();
        foreach (var path in Directory.EnumerateDirectories(runsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AgentHomeRunPaths.TryResolveRun(runsRoot, Path.GetFileName(path)) is { } run)
            {
                runs.Add(run);
            }
        }

        // Newest first, and only the requested window is opened: paging happens before any file is touched, so the
        // per-run reads below are bounded by the page size rather than by how many runs the node has ever made.
        runs.Sort(static (left, right) => right.StartedAt.CompareTo(left.StartedAt));

        var items = new List<AgentHomeRunSummary>();
        foreach (var (path, runId, startedAt) in runs.Skip(offset).Take(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await SummarizeAsync(path, runId, startedAt, cancellationToken));
        }

        return new AgentHomeRunPage { Items = items, TotalCount = runs.Count };
    }

    /// <inheritdoc />
    public async Task<AgentHomeRunText?> ReadLogAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (ResolveRun(runId) is not { } run)
        {
            return null;
        }

        // The same two windows the page is built from: an operator reading a run wants its opening and its end, and
        // the middle of a multi-gigabyte log is neither.
        var (lines, truncated) = await ReadBoundedLinesAsync(Path.Combine(run.Path, "logs", "events.jsonl"), cancellationToken);
        return new AgentHomeRunText { Text = string.Join('\n', lines), Truncated = truncated };
    }

    /// <inheritdoc />
    public async Task<AgentHomeRunText?> ReadPatchAsync(string runId, CancellationToken cancellationToken = default)
    {
        return ResolveRun(runId) is { } run
            ? await ReadCappedTextAsync(Path.Combine(run.Path, "patches", "changes.patch"), PatchDisplayMaxBytes, cancellationToken)
            : null;
    }

    private AgentHomeRunLocation? ResolveRun(string runId)
    {
        var runsRoot = AgentHomeRunPaths.ResolveRunsRoot(_options, _dataDirectoryRoot);
        return AgentHomeRunPaths.TryResolveRun(runsRoot, runId);
    }

    /// <summary>
    ///     The head of one run file as text, with whether the file went on past the cap.
    /// </summary>
    /// <remarks>
    ///     A run that exists but whose file is missing, linked, non-regular or unreadable reads as empty rather than
    ///     as unknown: the question "what did this run leave here" was answered, and "nothing this node will show you"
    ///     is the answer. The cut is made at the last complete line so a split UTF-8 sequence never reaches a viewer
    ///     as replacement characters dressed as content.
    /// </remarks>
    private static async Task<AgentHomeRunText> ReadCappedTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = TryOpenRunFile(path, long.MaxValue);
        if (stream is null)
        {
            return new AgentHomeRunText { Text = string.Empty, Truncated = false };
        }

        try
        {
            var truncated = stream.Length > maxBytes;
            var text = Encoding.UTF8.GetString(await ReadSegmentAsync(stream, offset: 0, (int)Math.Min(stream.Length, maxBytes), cancellationToken));
            if (truncated)
            {
                var lastBreak = text.LastIndexOf('\n');
                text = lastBreak < 0 ? string.Empty : text[..(lastBreak + 1)];
            }

            return new AgentHomeRunText { Text = text, Truncated = truncated };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new AgentHomeRunText { Text = string.Empty, Truncated = false };
        }
    }

    private static async Task<AgentHomeRunSummary> SummarizeAsync(string runDirectory,
        string runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var events = await ReadEventsAsync(Path.Combine(runDirectory, "logs", "events.jsonl"), cancellationToken);
        var patchPath = Path.Combine(runDirectory, "patches", "changes.patch");

        return new AgentHomeRunSummary
        {
            RunId = runId,
            StartedAtUtc = startedAt,
            Outcome = events.Outcome,
            ConversationId = events.ConversationId,
            ApplyState = events.ApplyState,
            PatchExported = File.Exists(patchPath),
            ChangedFileCount = await CountChangedFilesAsync(Path.Combine(runDirectory, "patches", "changed-files.json"), cancellationToken),
            SizeBytes = AgentHomeRunPaths.MeasureBytes(runDirectory, MaxMeasuredEntriesPerRun)
        };
    }

    /// <summary>The three facts a run's event log carries, or the unknown defaults when it carries none of them.</summary>
    private static async Task<EventSummary> ReadEventsAsync(string eventsPath, CancellationToken cancellationToken)
    {
        var summary = new EventSummary();
        foreach (var line in (await ReadBoundedLinesAsync(eventsPath, cancellationToken)).Lines)
        {
            if (line.Length <= MaxLogLineChars)
            {
                summary = ApplyLine(summary, line);
            }
        }

        return summary;
    }

    private static EventSummary ApplyLine(EventSummary summary, string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("eventName", out var eventName)
                   && eventName.ValueKind == JsonValueKind.String
                ? Apply(summary, eventName.GetString(), root)
                : summary;
        }
        catch (JsonException)
        {
            // A torn or hostile line is skipped; the rest of the log still answers the page.
            return summary;
        }
    }

    private static EventSummary Apply(EventSummary summary, string? eventName, JsonElement root)
    {
        switch (eventName)
        {
            case "started":
                return summary with { ConversationId = ReadConversationId(root) };
            case "cancelled":
                return summary with { Outcome = AgentHomeRunOutcomes.Cancelled };
            case "run_completed":
                return summary with { Outcome = ReadStatus(root) };
            case "patch_applied":
                return summary with { ApplyState = AgentHomeRunApplyStates.Applied };
            case "patch_apply_rejected":
                return summary with { ApplyState = AgentHomeRunApplyStates.Rejected };
            default:
                return summary;
        }
    }

    /// <summary>
    ///     The <c>status=</c> clause of a <c>run_completed</c> detail, mapped onto the closed outcome vocabulary.
    /// </summary>
    /// <remarks>
    ///     The rest of the detail line is discarded rather than parsed: it is a semicolon-joined free-text record, and
    ///     a value that is not one of the node's own status names is reported as unknown. That is what stops the list
    ///     from becoming a channel for whatever a run's log happens to contain.
    /// </remarks>
    private static string ReadStatus(JsonElement root)
    {
        if (!root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.String)
        {
            return AgentHomeRunOutcomes.Unknown;
        }

        var text = detail.GetString();
        if (text is null || !text.StartsWith("status=", StringComparison.Ordinal))
        {
            return AgentHomeRunOutcomes.Unknown;
        }

        var end = text.IndexOf(';', StringComparison.Ordinal);
        var status = end < 0 ? text["status=".Length..] : text["status=".Length..end];
        return AgentHomeRunOutcomes.CompletedStatuses.Contains(status) ? status : AgentHomeRunOutcomes.Unknown;
    }

    private static Guid? ReadConversationId(JsonElement root)
    {
        return root.TryGetProperty("conversationId", out var conversationId)
               && conversationId.ValueKind == JsonValueKind.String
               && Guid.TryParse(conversationId.GetString(), out var parsed)
            ? parsed
            : null;
    }

    /// <summary>How many entries <c>changed-files.json</c> holds; <see langword="null" /> when it cannot be counted.</summary>
    private static async Task<int?> CountChangedFilesAsync(string changedFilesPath, CancellationToken cancellationToken)
    {
        await using var stream = TryOpenRunFile(changedFilesPath, ChangedFilesMaxBytes);
        if (stream is null)
        {
            return null;
        }

        try
        {
            var buffer = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(buffer.AsMemory(), cancellationToken);
            using var document = JsonDocument.Parse(buffer);
            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Opens one file a run owns, or answers <see langword="null" /> so the caller reports its unknown value.
    /// </summary>
    /// <remarks>
    ///     The single gate for every run file this service reads, because a run directory is the one place on the node
    ///     where a model-influenced process wrote. Three refusals, all from ONE stat before the open: a link, which
    ///     can point anywhere; a path with no content, which is what a FIFO, socket or device stats as — and opening
    ///     one of those blocks a thread forever, since .NET offers no non-blocking open and no file-type signal to
    ///     refuse it by; and a file past the caller's cap. Reads never block a writer, and never throw past here.
    /// </remarks>
    private static FileStream? TryOpenRunFile(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length <= 0 || info.Length > maxBytes)
            {
                return null;
            }

            return new FileStream(path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    // The run logger may still be appending; a reader that locked it out would break the run, not
                    // just the page.
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Complete lines from the head and tail of a JSONL file, never the middle of a large one, plus whether a
    ///     middle was skipped at all.
    /// </summary>
    /// <remarks>
    ///     The <c>started</c> event is the first line and the outcome and apply events are the last few, so two capped
    ///     windows answer everything the list asks. A partial line at a window's edge is dropped rather than parsed.
    ///     The flag exists for the reader that SHOWS these lines: joined text with a gap in it and no note would read
    ///     as a complete log that simply says nothing about the middle of the run.
    /// </remarks>
    private static async Task<(IReadOnlyList<string> Lines, bool Truncated)> ReadBoundedLinesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = TryOpenRunFile(path, EventsMaxBytes);
        if (stream is null)
        {
            return ([], false);
        }

        byte[] head;
        byte[] tail;
        try
        {
            var length = stream.Length;
            if (length <= EventsHeadBytes + EventsTailBytes)
            {
                // Small enough that both windows would touch: read it once, so no line is lost to a seam between
                // them. The cap is still what bounds the read.
                head = await ReadSegmentAsync(stream, offset: 0, (int)length, cancellationToken);
                tail = [];
            }
            else
            {
                head = await ReadSegmentAsync(stream, offset: 0, EventsHeadBytes, cancellationToken);
                tail = await ReadSegmentAsync(stream, length - EventsTailBytes, EventsTailBytes, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ([], false);
        }

        // Each window drops the edge line it cannot have in full: the head ends mid-line when it hit its cap, and a
        // tail taken at all starts mid-line.
        return
        ([
            .. SplitLines(head, dropFirst: false, dropLast: tail.Length > 0),
            .. SplitLines(tail, dropFirst: true, dropLast: false)
        ], tail.Length > 0);
    }

    private static async Task<byte[]> ReadSegmentAsync(FileStream stream, long offset, int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return [];
        }

        var buffer = new byte[count];
        stream.Position = offset;
        await stream.ReadExactlyAsync(buffer.AsMemory(start: 0, count), cancellationToken);
        return buffer;
    }

    private static IEnumerable<string> SplitLines(byte[] segment, bool dropFirst, bool dropLast)
    {
        if (segment.Length == 0)
        {
            yield break;
        }

        var lines = Encoding.UTF8.GetString(segment).Split('\n');
        var start = dropFirst ? 1 : 0;
        var end = dropLast ? lines.Length - 1 : lines.Length;
        for (var index = start; index < end; index++)
        {
            var line = lines[index].Trim('\r');
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private readonly record struct EventSummary
    {
        public string Outcome { get; init; }

        public string ApplyState { get; init; }

        public Guid? ConversationId { get; init; }

        public EventSummary()
        {
            Outcome = AgentHomeRunOutcomes.Unknown;
            ApplyState = AgentHomeRunApplyStates.None;
            ConversationId = null;
        }
    }
}
