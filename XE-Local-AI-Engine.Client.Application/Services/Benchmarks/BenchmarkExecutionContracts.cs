namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public interface IBenchmarkRunExecutor
{
    Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken);
}

public interface IBenchmarkJudgeExecutor
{
    Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken);
}

public enum BenchmarkRunStreamEventKind
{
    OutputDelta,
    ReasoningDelta,
    ToolCall,
    ToolResult,
    PrimaryState,
    JudgeState,
    Metrics,
    TerminalSnapshotAvailable
}

public sealed record BenchmarkRunStreamPayload(
    string? Content = null,
    string? State = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? Arguments = null,
    string? Result = null,
    bool? IsError = null,
    int? EffectiveContextTokens = null,
    long? DurationMs = null,
    int? TotalTokens = null,
    double? TokensPerSecond = null,
    long? RunVersion = null,
    double? TtftMs = null,
    int? PromptTokens = null,
    double? PromptTokensPerSecond = null,
    int? GenerationTokens = null,
    double? GenerationTokensPerSecond = null,
    int? CachedPromptTokens = null,
    int? SegmentCount = null);

public sealed record BenchmarkRunStreamEvent(
    Guid RunId,
    long Sequence,
    BenchmarkRunStreamEventKind Kind,
    BenchmarkRunStreamPayload Payload);

public sealed class BenchmarkRunStreamEventArgs(BenchmarkRunStreamEvent streamEvent) : EventArgs
{
    public BenchmarkRunStreamEvent StreamEvent { get; } = streamEvent ?? throw new ArgumentNullException(nameof(streamEvent));
}

public sealed record BenchmarkReplayResult(
    IReadOnlyList<BenchmarkRunStreamEvent> Events,
    bool ResetRequired,
    long LatestSequence,
    long RunVersion);

public interface IBenchmarkEventBuffer
{
    event EventHandler<BenchmarkRunStreamEventArgs>? EventPublished;

    BenchmarkRunStreamEvent Append(Guid runId, BenchmarkRunStreamEventKind kind, BenchmarkRunStreamPayload payload);
    BenchmarkRunStreamEvent Reserve(Guid runId, BenchmarkRunStreamEventKind kind, BenchmarkRunStreamPayload payload);
    void PublishReserved(BenchmarkRunStreamEvent streamEvent);
    BenchmarkReplayResult Replay(Guid runId, long afterSequence, long runVersion);
    void BeginActivePhase(Guid runId, long persistedSequence);
    void EvictPlaintext(Guid runId);
}

public sealed class BenchmarkEventBufferOptions
{
    public const int DefaultMaxEventCount = 512;
    public const int DefaultMaxUtf8Bytes = 1024 * 1024;

    public int MaxEventCount { get; init; } = DefaultMaxEventCount;
    public int MaxUtf8Bytes { get; init; } = DefaultMaxUtf8Bytes;
}

public sealed class BenchmarkEventBuffer : IBenchmarkEventBuffer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly int _maxEventCount;
    private readonly int _maxUtf8Bytes;
    private readonly Dictionary<Guid, RunBuffer> _runs = [];

    public BenchmarkEventBuffer(IOptions<BenchmarkEventBufferOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxEventCount = options.Value.MaxEventCount;
        _maxUtf8Bytes = options.Value.MaxUtf8Bytes;
        if (_maxEventCount <= 0 || _maxUtf8Bytes <= 0)
        {
            throw new InvalidOperationException("Benchmark event buffer limits must be positive.");
        }
    }

    public event EventHandler<BenchmarkRunStreamEventArgs>? EventPublished;

    public BenchmarkRunStreamEvent Append(Guid runId, BenchmarkRunStreamEventKind kind, BenchmarkRunStreamPayload payload)
    {
        var streamEvent = Reserve(runId, kind, payload);
        PublishReserved(streamEvent);
        return streamEvent;
    }

    public BenchmarkRunStreamEvent Reserve(Guid runId, BenchmarkRunStreamEventKind kind, BenchmarkRunStreamPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_gate)
        {
            var state = GetOrCreate(runId);
            return new BenchmarkRunStreamEvent(runId, ++state.LatestSequence, kind, payload);
        }
    }

    public void PublishReserved(BenchmarkRunStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        lock (_gate)
        {
            var state = GetOrCreate(streamEvent.RunId);
            if (streamEvent.Sequence <= state.LastPublishedSequence)
            {
                return;
            }

            if (streamEvent.Sequence > state.LatestSequence)
            {
                throw new InvalidOperationException("A benchmark stream event must be reserved before it is published.");
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(streamEvent, JsonOptions).Length;
            state.Events.AddLast(new BufferedEvent(streamEvent, bytes));
            state.Utf8Bytes += bytes;
            state.LastPublishedSequence = streamEvent.Sequence;
            Trim(state);
        }

        EventPublished?.Invoke(this, new BenchmarkRunStreamEventArgs(streamEvent));
    }

    public BenchmarkReplayResult Replay(Guid runId, long afterSequence, long runVersion)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var state))
            {
                return new BenchmarkReplayResult([], ResetRequired: false, LatestSequence: 0, runVersion);
            }

            var firstRetained = state.Events.First?.Value.Event.Sequence;
            var reset = state.PlaintextEvicted
                        || firstRetained is { } first && afterSequence < first - 1
                        || firstRetained is null && state.HistoryTruncated && afterSequence < state.LatestSequence;
            if (reset)
            {
                return new BenchmarkReplayResult([], ResetRequired: true, state.LatestSequence, runVersion);
            }

            var events = state.Events.Where(item => item.Event.Sequence > afterSequence).Select(item => item.Event).ToArray();
            return new BenchmarkReplayResult(events, ResetRequired: false, state.LatestSequence, runVersion);
        }
    }

    public void BeginActivePhase(Guid runId, long persistedSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(persistedSequence);

        lock (_gate)
        {
            var state = GetOrCreate(runId);
            var hadHistory = state.LatestSequence > 0 || state.Events.Count > 0 || state.PlaintextEvicted;
            state.Events.Clear();
            state.Utf8Bytes = 0;
            state.LatestSequence = Math.Max(state.LatestSequence, persistedSequence);
            state.LastPublishedSequence = Math.Max(state.LastPublishedSequence, persistedSequence);
            state.HistoryTruncated = hadHistory || persistedSequence > 0;
            state.PlaintextEvicted = false;
        }
    }

    public void EvictPlaintext(Guid runId)
    {
        lock (_gate)
        {
            var state = GetOrCreate(runId);
            state.Events.Clear();
            state.Utf8Bytes = 0;
            state.PlaintextEvicted = true;
        }
    }

    private RunBuffer GetOrCreate(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Benchmark run id must be non-empty.", nameof(runId));
        }

        if (!_runs.TryGetValue(runId, out var state))
        {
            state = new RunBuffer();
            _runs.Add(runId, state);
        }

        return state;
    }

    private void Trim(RunBuffer state)
    {
        while (state.Events.Count > _maxEventCount || state.Utf8Bytes > _maxUtf8Bytes)
        {
            var first = state.Events.First;
            if (first is null)
            {
                break;
            }

            state.Utf8Bytes -= first.Value.Utf8Bytes;
            state.Events.RemoveFirst();
            state.HistoryTruncated = true;
        }
    }

    private sealed class RunBuffer
    {
        public long LatestSequence { get; set; }
        public long LastPublishedSequence { get; set; }
        public int Utf8Bytes { get; set; }
        public bool PlaintextEvicted { get; set; }
        public bool HistoryTruncated { get; set; }
        public LinkedList<BufferedEvent> Events { get; } = [];
    }

    private sealed record BufferedEvent(BenchmarkRunStreamEvent Event, int Utf8Bytes);
}

public sealed record BenchmarkOutputPart(
    string Kind,
    string? Content = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? Arguments = null,
    string? Result = null,
    bool? IsError = null);

/// <summary>
///     Shaping for a run's output parts. The live capture appends ONE part per stream delta, so a thinking model's turn
///     arrives as thousands of <c>{"kind":"reasoning","content":" 5"}</c> parts (measured: 476 KB of JSON for a 4.3k-token
///     answer). Nothing downstream wants that granularity: the terminal write stores the COALESCED form, and the judge
///     grades a further-reduced projection of it. The part schema is unchanged either way — same kinds, same property
///     names — so every existing reader (the endpoint DTO, the live pane, the transcript viewer) is unaffected.
/// </summary>
public static class BenchmarkOutputParts
{
    public const string OutputKind = "output";
    public const string ReasoningKind = "reasoning";

    /// <summary>Appended to the last text part the judge is shown when the answer had to be cut to fit its context.</summary>
    public const string TruncationMarker = "\n\n[truncated: the primary output exceeded the judge context budget]";

    // ponytail: a coarse character allowance rather than a second context budgeter. Four characters per token mirrors
    // HeuristicTokenEstimator's divisor, and half the window is left for the system prompt, task, rubric, output schema
    // and the judge's own verdict. Ceiling: tool arguments and results are not counted, so a tool-heavy transcript can
    // still overrun. Upgrade path is to budget the BUILT payload with ITokenEstimator if that ever bites.
    private const int EstimatedCharsPerToken = 4;
    private const int MinimumJudgeTextChars = 2048;

    /// <summary>
    ///     Merges adjacent text parts of the same kind (output with output, reasoning with reasoning) into one part.
    ///     Tool-call and tool-result parts pass through untouched and act as boundaries, so the transcript order is
    ///     preserved exactly — text before a tool call never merges with text after it.
    /// </summary>
    public static IReadOnlyList<BenchmarkOutputPart> Coalesce(IEnumerable<BenchmarkOutputPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        List<BenchmarkOutputPart> merged = [];
        var text = new StringBuilder();
        string? pendingKind = null;
        foreach (var part in parts)
        {
            var isText = part.Kind is OutputKind or ReasoningKind;
            if (isText && string.Equals(pendingKind, part.Kind, StringComparison.Ordinal))
            {
                _ = text.Append(part.Content);
                continue;
            }

            if (pendingKind is not null)
            {
                merged.Add(new BenchmarkOutputPart(pendingKind, Content: text.ToString()));
                _ = text.Clear();
                pendingKind = null;
            }

            if (isText)
            {
                pendingKind = part.Kind;
                _ = text.Append(part.Content);
            }
            else
            {
                merged.Add(part);
            }
        }

        if (pendingKind is not null)
        {
            merged.Add(new BenchmarkOutputPart(pendingKind, Content: text.ToString()));
        }

        return merged;
    }

    /// <summary>
    ///     The parts the judge is shown: <see cref="Coalesce" />d, with every <c>reasoning</c> part DROPPED. Hidden
    ///     chain-of-thought is not the graded answer — the rubric evaluates the visible output — and on a thinking model
    ///     the reasoning alone blew the judge context (measured: 107,192 estimated tokens against a 16,384 window, so
    ///     every judging failed before inference). Text and tool parts are kept, in order.
    ///     <para>
    ///         Defensively bounded: if the reasoning-free text still cannot plausibly fit
    ///         <paramref name="judgeContextTokens" />, the tail is cut and the cut is marked with
    ///         <see cref="TruncationMarker" /> so the judge grades a visibly partial answer instead of the judging
    ///         failing outright. Truncation applies to the judge's copy only; the stored transcript keeps every part.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<BenchmarkOutputPart> ForJudge(IEnumerable<BenchmarkOutputPart> parts, int judgeContextTokens)
    {
        var graded = Coalesce(parts)
                     .Where(static part => !string.Equals(part.Kind, ReasoningKind, StringComparison.Ordinal))
                     .ToArray();
        var allowance = Math.Max(MinimumJudgeTextChars, judgeContextTokens / 2 * EstimatedCharsPerToken);
        if (graded.Sum(static part => part.Content?.Length ?? 0) <= allowance)
        {
            return graded;
        }

        List<BenchmarkOutputPart> bounded = [];
        var remaining = allowance;
        foreach (var part in graded)
        {
            if (part.Content is not { } content)
            {
                bounded.Add(part);
                continue;
            }

            if (content.Length <= remaining)
            {
                bounded.Add(part);
                remaining -= content.Length;
                continue;
            }

            if (remaining > 0)
            {
                bounded.Add(part with
                {
                    Content = string.Concat(content.AsSpan(0, remaining), TruncationMarker)
                });
            }

            remaining = 0;
        }

        return bounded;
    }
}

public sealed class BenchmarkContextAdmissionPolicy(int requiredContextTokens) : IInvocationGenerationAdmissionPolicy
{
    private readonly int _requiredContextTokens = requiredContextTokens > 0
        ? requiredContextTokens
        : throw new ArgumentOutOfRangeException(nameof(requiredContextTokens));

    public int? EffectiveContextTokens { get; private set; }

    public Task<InvocationGenerationAdmissionDecision> EvaluateAsync(InvocationGenerationAdmissionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        EffectiveContextTokens = context.EffectiveContextTokens;
        return Task.FromResult(context.EffectiveContextTokens switch
        {
            null => InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable),
            < 1 => InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable),
            var effective when effective < _requiredContextTokens =>
                InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient),
            _ => InvocationGenerationAdmissionDecision.Allow
        });
    }
}

internal static class BenchmarkSnapshotModelComparer
{
    public static bool Matches(BenchmarkInstalledModelSnapshotV1 expected, InstalledModelSnapshot actual)
    {
        return string.Equals(expected.ModelName, actual.ModelName, StringComparison.Ordinal)
               && string.Equals(expected.RegistryRevision, actual.RegistryRevision, StringComparison.Ordinal)
               && string.Equals(expected.RegistryAliasSetHash, actual.RegistryAliasSetHash, StringComparison.Ordinal)
               && string.Equals(expected.PhysicalMemberSetHash, actual.PhysicalMemberSetHash, StringComparison.Ordinal)
               && expected.Origin == actual.Origin
               && string.Equals(expected.ProviderName, actual.ProviderName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(expected.ProviderMappingRevision, actual.ProviderMappingRevision, StringComparison.Ordinal)
               && string.Equals(expected.ModelContentFingerprint, actual.ModelContentFingerprint, StringComparison.Ordinal)
               && Aliases(expected.RegistryAliases).SequenceEqual(Aliases(actual.RegistryAliases), StringComparer.Ordinal)
               && Members(expected.Members).SequenceEqual(Members(actual.Members), StringComparer.Ordinal);
    }

    private static IEnumerable<string> Aliases(IEnumerable<BenchmarkRegistryAliasSnapshotV1> aliases) =>
        aliases.Select(static alias => $"{alias.ModelName}\u001f{alias.RegistryRevision}").Order(StringComparer.Ordinal);

    private static IEnumerable<string> Aliases(IEnumerable<InstalledModelRegistryAliasSnapshot> aliases) =>
        aliases.Select(static alias => $"{alias.ModelName}\u001f{alias.RegistryRevision}").Order(StringComparer.Ordinal);

    private static IEnumerable<string> Members(IEnumerable<BenchmarkPhysicalMemberSnapshotV1> members) =>
        members.Select(static member => Member(member.RelativePath,
                   member.Role,
                   member.SizeBytes,
                   member.Sha256,
                   member.OwningAliases,
                   member.Required,
                   member.MetadataSchemaVersion,
                   member.MemberFingerprint))
               .Order(StringComparer.Ordinal);

    private static IEnumerable<string> Members(IEnumerable<InstalledModelPhysicalMember> members) =>
        members.Select(static member => Member(member.RelativePath,
                   member.Role,
                   member.SizeBytes,
                   member.Sha256,
                   member.OwningAliases,
                   member.Required,
                   member.MetadataSchemaVersion,
                   member.MemberFingerprint))
               .Order(StringComparer.Ordinal);

    private static string Member(string path,
        InstalledModelPhysicalMemberRole role,
        long size,
        string sha256,
        IEnumerable<string> owners,
        bool required,
        int? schema,
        string? fingerprint) =>
        string.Join('\u001f', path, role, size, sha256, string.Join('\u001e', owners.Order(StringComparer.Ordinal)), required, schema, fingerprint);
}

/// <summary>
///     The ONLY serializer for the benchmark run's stored <c>output_parts_json</c> blob (the judge's own blobs ride
///     <c>BenchmarkJudgeSerialization</c>). Public because a reader must never re-derive the options at the call site:
///     <see cref="JsonSerializerDefaults.Web" /> is camelCase, so deserializing with default options binds every
///     property to its default and hands the API a zeroed payload instead of failing.
/// </summary>
public static class BenchmarkExecutionSerialization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] SerializeParts(IEnumerable<BenchmarkOutputPart> parts) =>
        JsonSerializer.SerializeToUtf8Bytes(parts, JsonOptions);

    public static IReadOnlyList<BenchmarkOutputPart> DeserializeParts(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<BenchmarkOutputPart[]>(payload, JsonOptions)
        ?? throw new BenchmarkSnapshotException("Benchmark output parts are invalid.");
}

public sealed record BenchmarkEligibleAgent(Guid Id, string Name, int Version);

public sealed record BenchmarkEligibleModel(
    string ModelName,
    int? MaxContextTokens,
    int? EffectiveContextTokens,
    LocalModelOrigin? Origin,
    string ModelContentFingerprint,
    bool SupportsTools);

/// <summary>
///     The operator-editable judge configuration. Everything here is inside the policy hash, so any change to it is a
///     new policy revision and — on a project that already has runs — a re-judge.
/// </summary>
/// <param name="Rubric">The weighted criteria; <see langword="null" /> takes <see cref="BenchmarkJudgeRubricDefaults.Default" />.</param>
public sealed record BenchmarkJudgePolicyDraft(
    string ModelName,
    int ContextTokens,
    BenchmarkJudgeRubricV1? Rubric = null,
    string? ReferenceAnswer = null);

/// <param name="MaxOutputTokens">
///     The per-run output-token budget frozen into every run's sampling, or <see langword="null" /> to leave generation
///     context-limited. Validated as <c>1 &lt;= MaxOutputTokens &lt; ContextTokens</c>.
/// </param>
public sealed record BenchmarkProjectDraft(
    Guid Id,
    string Name,
    string CoreTask,
    int ContextTokens,
    Guid AgentDefinitionId,
    BenchmarkJudgePolicyDraft? Judge = null,
    int? MaxOutputTokens = null,
    int? InvocationTimeoutSeconds = null);

public sealed class BenchmarkQueueOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}
