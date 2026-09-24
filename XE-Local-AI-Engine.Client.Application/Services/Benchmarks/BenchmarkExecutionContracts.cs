namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
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

public sealed record BenchmarkRunStreamPayload
{
    public string? Content { get; init; }

    public string? State { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public string? Arguments { get; init; }

    public string? Result { get; init; }

    public bool? IsError { get; init; }

    public int? EffectiveContextTokens { get; init; }

    public long? DurationMs { get; init; }

    public int? TotalTokens { get; init; }

    public double? TokensPerSecond { get; init; }

    public long? RunVersion { get; init; }

    public double? TtftMs { get; init; }

    public int? PromptTokens { get; init; }

    public double? PromptTokensPerSecond { get; init; }

    public int? GenerationTokens { get; init; }

    public double? GenerationTokensPerSecond { get; init; }

    public int? CachedPromptTokens { get; init; }

    public int? SegmentCount { get; init; }
}

public sealed record BenchmarkRunStreamEvent
{
    public required Guid RunId { get; init; }

    public required long Sequence { get; init; }

    public required BenchmarkRunStreamEventKind Kind { get; init; }

    public required BenchmarkRunStreamPayload Payload { get; init; }
}

public sealed class BenchmarkRunStreamEventArgs : EventArgs
{
    public BenchmarkRunStreamEventArgs(BenchmarkRunStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        StreamEvent = streamEvent;
    }

    public BenchmarkRunStreamEvent StreamEvent { get; }
}

public sealed class BenchmarkReplayResult
{
    public required IReadOnlyList<BenchmarkRunStreamEvent> Events { get; init; }

    public required bool ResetRequired { get; init; }

    public required long LatestSequence { get; init; }

    public required long RunVersion { get; init; }
}

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

    /// <summary>How many TERMINAL runs keep their (already emptied) buffer entry.</summary>
    /// <remarks>
    ///     The entry carries no output — eviction cleared that — only the sequence bookkeeping that lets a late
    ///     subscriber be told to reset instead of being answered with silence. Past this many the oldest are dropped:
    ///     the hub then compares the run's persisted <c>LastStreamSequence</c> against an empty replay and resets
    ///     anyway, which is the same answer by another route. Active runs are never dropped, however many there are.
    /// </remarks>
    public const int DefaultMaxRetainedTerminalRuns = 256;

    public int MaxEventCount { get; init; } = DefaultMaxEventCount;
    public int MaxUtf8Bytes { get; init; } = DefaultMaxUtf8Bytes;
    public int MaxRetainedTerminalRuns { get; init; } = DefaultMaxRetainedTerminalRuns;
}

public sealed class BenchmarkEventBuffer : IBenchmarkEventBuffer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly int _maxEventCount;
    private readonly int _maxUtf8Bytes;
    private readonly int _maxRetainedTerminalRuns;
    private readonly Dictionary<Guid, RunBuffer> _runs = [];

    /// <summary>Terminal runs in eviction order, so the oldest tombstone is the one dropped when the cap is reached.</summary>
    private readonly Queue<Guid> _evicted = new();

    public BenchmarkEventBuffer(IOptions<BenchmarkEventBufferOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxEventCount = options.Value.MaxEventCount;
        _maxUtf8Bytes = options.Value.MaxUtf8Bytes;
        _maxRetainedTerminalRuns = options.Value.MaxRetainedTerminalRuns;
        if (_maxEventCount <= 0 || _maxUtf8Bytes <= 0 || _maxRetainedTerminalRuns <= 0)
        {
            throw new InvalidOperationException("Benchmark event buffer limits must be positive.");
        }
    }

    /// <summary>How many runs the buffer still holds an entry for. Test-only seam.</summary>
    internal int TrackedRunCount
    {
        get
        {
            lock (_gate)
            {
                return _runs.Count;
            }
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
            return new BenchmarkRunStreamEvent
            {
                RunId = runId,
                Sequence = ++state.LatestSequence,
                Kind = kind,
                Payload = payload
            };
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
            state.Events.AddLast(new BufferedEvent
            {
                Event = streamEvent,
                Utf8Bytes = bytes
            });
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
                return new BenchmarkReplayResult
                {
                    Events = [],
                    ResetRequired = false,
                    LatestSequence = 0,
                    RunVersion = runVersion
                };
            }

            var firstRetained = state.Events.First?.Value.Event.Sequence;
            var reset = state.PlaintextEvicted
                        || firstRetained is { } first && afterSequence < first - 1
                        || firstRetained is null && state.HistoryTruncated && afterSequence < state.LatestSequence;
            if (reset)
            {
                return new BenchmarkReplayResult
                {
                    Events = [],
                    ResetRequired = true,
                    LatestSequence = state.LatestSequence,
                    RunVersion = runVersion
                };
            }

            var events = state.Events.Where(item => item.Event.Sequence > afterSequence).Select(item => item.Event).ToArray();
            return new BenchmarkReplayResult
            {
                Events = events,
                ResetRequired = false,
                LatestSequence = state.LatestSequence,
                RunVersion = runVersion
            };
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

            // The entry survives eviction on purpose: emptied, it still turns a late subscriber's replay into a reset rather than silence. It is also the leak, so the tombstones are capped.
            // Queued is its OWN flag, not PlaintextEvicted: a run is evicted once per terminal PHASE and has two, and BeginActivePhase clears it between them — keying the queue off it halves the cap.
            state.PlaintextEvicted = true;
            if (!state.Queued)
            {
                state.Queued = true;
                _evicted.Enqueue(runId);
            }

            while (_evicted.Count > _maxRetainedTerminalRuns)
            {
                var oldest = _evicted.Dequeue();

                // Skipped when the run went active again (a judge phase after the primary): its entry belongs to a live stream, and dropping it would restart that stream's sequence numbering.
                // Either way the id leaves the queue, so a run that is spared here can be enqueued again by its next eviction.
                if (_runs.TryGetValue(oldest, out var stale))
                {
                    stale.Queued = false;
                    if (stale.PlaintextEvicted)
                    {
                        _ = _runs.Remove(oldest);
                    }
                }
            }
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

        /// <summary>Whether this run's id is currently in the tombstone queue. Owned by the queue, not by a phase.</summary>
        public bool Queued { get; set; }

        public bool HistoryTruncated { get; set; }
        public LinkedList<BufferedEvent> Events { get; } = [];
    }

    private sealed record BufferedEvent
    {
        public required BenchmarkRunStreamEvent Event { get; init; }

        public required int Utf8Bytes { get; init; }
    }
}

public sealed record BenchmarkOutputPart(
    string Kind,
    string? Content = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? Arguments = null,
    string? Result = null,
    bool? IsError = null);

/// <summary>Shaping for a run's output parts.</summary>
/// <remarks>
///     The live capture appends ONE part per stream delta, so a thinking model's turn arrives as thousands of
///     <c>{"kind":"reasoning","content":" 5"}</c> parts (measured: 476 KB of JSON for a 4.3k-token answer). Nothing
///     downstream wants that granularity: the terminal write stores the COALESCED form and the judge grades a
///     further-reduced projection of it. The part schema is unchanged either way — same kinds, same property names —
///     so every existing reader (the endpoint DTO, the live pane, the transcript viewer) is unaffected.
/// </remarks>
public static class BenchmarkOutputParts
{
    public const string OutputKind = "output";
    public const string ReasoningKind = "reasoning";

    public const string ToolCallKind = "tool_call";
    public const string ToolResultKind = "tool_result";

    /// <summary>Appended to the last text part the judge is shown when the answer had to be cut to fit its context.</summary>
    public const string TruncationMarker = "\n\n[truncated: the primary output exceeded the judge context budget]";

    // simplified: a coarse character allowance, not a second context budgeter — four chars per token mirrors HeuristicTokenEstimator's divisor, half the window left for the rest of the judge payload.
    // Ceiling: tool arguments and results are not counted, so a tool-heavy transcript can still overrun. Upgrade path: budget the BUILT payload with ITokenEstimator.
    private const int EstimatedCharsPerToken = 4;
    private const int MinimumJudgeTextChars = 2048;

    /// <summary>
    ///     Merges adjacent text parts of the same kind (output with output, reasoning with reasoning) into one part.
    /// </summary>
    /// <remarks>
    ///     Tool-call and tool-result parts pass through untouched and act as boundaries, so the transcript order is
    ///     preserved exactly — text before a tool call never merges with text after it.
    /// </remarks>
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
    ///     Whether any VISIBLE answer text was emitted — reasoning excluded, whitespace not counted. The narrow half of
    ///     <see cref="IsUnanswered" />, separate because a run cut off at the token budget is asked only this: did it
    ///     ever leave the scratchpad?
    /// </summary>
    public static bool HasAnswerText(IReadOnlyList<BenchmarkOutputPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Any(static part => string.Equals(part.Kind, OutputKind, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(part.Content));
    }

    /// <summary>Whether the turn produced no gradable answer, judged from the parts alone.</summary>
    /// <remarks>
    ///     Two shapes, both of which a provider reports as a CLEAN finish: the transcript ENDS on a <c>tool_call</c> (no
    ///     <c>tool_result</c> and no answer ever followed), or the reasoning-stripped text is empty or whitespace (a
    ///     thinking model spent the whole turn in its scratchpad). Either way the run reports <c>stop</c> or
    ///     <c>tool_calls</c>, which reads downstream as a finished answer: the judge grades an empty transcript and the
    ///     ranking seats the score beside runs that actually answered.
    /// </remarks>
    /// <param name="parts">
    ///     The COALESCED parts: a raw capture's last part is whatever fragment arrived last, not the shape of the turn.
    /// </param>
    public static bool IsUnanswered(IReadOnlyList<BenchmarkOutputPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (!HasAnswerText(parts))
        {
            return true;
        }

        // A tool_call as the FINAL part is by construction one no tool_result ever answered: the transcript is in turn
        // order, so nothing follows it. An earlier unmatched id is a provider quirk, not an unfinished turn.
        return parts.Count > 0 && string.Equals(parts[^1].Kind, ToolCallKind, StringComparison.Ordinal);
    }

    /// <summary>The parts the judge is shown: <see cref="Coalesce" />d, with every <c>reasoning</c> part DROPPED.</summary>
    /// <remarks>
    ///     Hidden chain-of-thought is not the graded answer — the rubric evaluates the visible output — and on a
    ///     thinking model the reasoning alone blew the judge context (measured: 107,192 estimated tokens against a
    ///     16,384 window, so every judging failed before inference). Text and tool parts are kept, in order. Text that
    ///     still cannot plausibly fit <paramref name="judgeContextTokens" /> is cut and marked with
    ///     <see cref="TruncationMarker" />; the cut applies to the judge's copy only, never the stored transcript.
    /// </remarks>
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

public sealed class BenchmarkContextAdmissionPolicy : IInvocationGenerationAdmissionPolicy
{
    private readonly int _requiredContextTokens;

    public BenchmarkContextAdmissionPolicy(int requiredContextTokens)
    {
        _requiredContextTokens = requiredContextTokens > 0
            ? requiredContextTokens
            : throw new ArgumentOutOfRangeException(nameof(requiredContextTokens));
    }

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
///     <c>BenchmarkJudgeSerialization</c>).
/// </summary>
/// <remarks>
///     Public because a reader must never re-derive the options at the call site: <see cref="JsonSerializerDefaults.Web" />
///     is camelCase, so deserializing with default options binds every property to its default and hands the API a
///     zeroed payload instead of failing.
/// </remarks>
public static class BenchmarkExecutionSerialization
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] SerializeParts(IEnumerable<BenchmarkOutputPart> parts) =>
        JsonSerializer.SerializeToUtf8Bytes(parts, JsonOptions);

    public static IReadOnlyList<BenchmarkOutputPart> DeserializeParts(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<BenchmarkOutputPart[]>(payload, JsonOptions)
        ?? throw new BenchmarkSnapshotException("Benchmark output parts are invalid.");
}

public sealed class BenchmarkEligibleAgent
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Version { get; init; }
}

public sealed class BenchmarkEligibleModel
{
    public required string ModelName { get; init; }

    public required int? MaxContextTokens { get; init; }

    public required int? EffectiveContextTokens { get; init; }

    public required LocalModelOrigin? Origin { get; init; }

    public required string ModelContentFingerprint { get; init; }

    public required bool SupportsTools { get; init; }
}

/// <summary>
///     The operator-editable judge configuration. Everything here is inside the policy hash, so any change to it is a
///     new policy revision and — on a project that already has runs — a re-judge.
/// </summary>
public sealed record BenchmarkJudgePolicyDraft
{
    public required string ModelName { get; init; }

    public required int ContextTokens { get; init; }

    /// <summary>The weighted criteria; <see langword="null" /> takes <see cref="BenchmarkJudgeRubricDefaults.Default" />.</summary>
    public BenchmarkJudgeRubricV1? Rubric { get; init; }

    public string? ReferenceAnswer { get; init; }

    /// <summary>
    ///     <c>pointwise</c> (the default and the only mode this build executes) or <c>pairwise</c>. Absent means
    ///     pointwise, so a caller written before the mode existed keeps working.
    /// </summary>
    public string? Mode { get; init; }
}

/// <summary>
///     The four settable quant-fidelity knobs. The base model's FINGERPRINT is absent on purpose: the service resolves
///     it from the eligible-model catalog, because it is an input to the KLD comparability digest.
/// </summary>
public sealed class BenchmarkProjectFidelitySettings
{
    public required bool Enabled { get; init; }

    public required bool KldEnabled { get; init; }

    public required int? Chunks { get; init; }

    public required string? KldBaseModelName { get; init; }
}

public sealed record BenchmarkProjectDraft
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string CoreTask { get; init; }

    public required int ContextTokens { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public BenchmarkJudgePolicyDraft? Judge { get; init; }

    /// <summary>
    ///     The per-run output-token budget frozen into every run's sampling, or <see langword="null" /> to leave generation
    ///     context-limited. Validated as <c>1 &lt;= MaxOutputTokens &lt; ContextTokens</c>.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    public int? InvocationTimeoutSeconds { get; init; }

    /// <summary>
    ///     The per-run thinking budget frozen into every run's sampling, or <see langword="null" /> to leave the
    ///     reasoning bounded only by the effort ladder and the window.
    /// </summary>
    /// <remarks>
    ///     Validated as <c>1 &lt;= ReasoningBudgetTokens &lt; ContextTokens</c>, and — with an output budget also set —
    ///     as leaving a prompt reserve inside the context.
    /// </remarks>
    public int? ReasoningBudgetTokens { get; init; }

    public bool FidelityEnabled { get; init; }

    public bool FidelityKldEnabled { get; init; }

    public int? FidelityChunks { get; init; }

    /// <summary>
    ///     The base model KL divergence is measured against. Its FINGERPRINT is never part of a draft: the service
    ///     resolves it from the eligible-model catalog, so a caller cannot claim a base identity the node does not have.
    /// </summary>
    public string? FidelityKldBaseModelName { get; init; }
}

public sealed class BenchmarkQueueOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Benchmarks:Queue";

    /// <summary>The longest poll interval that still lets a queued run start promptly after a signal is missed.</summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
///     Fails the node's start rather than the first poll: the hosted service already refused a non-positive interval,
///     but it did so from a background thread after boot, where the operator sees a log line instead of a failure.
/// </summary>
internal sealed class BenchmarkQueueOptionsValidator : IValidateOptions<BenchmarkQueueOptions>
{
    public ValidateOptionsResult Validate(string? name, BenchmarkQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PollInterval > TimeSpan.Zero && options.PollInterval <= BenchmarkQueueOptions.MaxPollInterval
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{BenchmarkQueueOptions.SectionName}:PollInterval must be positive and at most "
                                         + $"{BenchmarkQueueOptions.MaxPollInterval}.");
    }
}

/// <summary>
///     One launch request: a project, a model, and how many measured runs to enqueue against them.
/// </summary>
public sealed record BenchmarkRunStartRequest
{
    public required Guid ProjectId { get; init; }

    public required string PrimaryModelName { get; init; }

    public required long ExpectedProjectVersion { get; init; }

    /// <summary>
    ///     The KV-cache type the run asked for, or <see langword="null" /> for Auto (freeze picks). Must already be
    ///     canonical — see <see cref="BenchmarkKvCacheType.TryNormalize" />.
    /// </summary>
    public string? KvCacheType { get; init; }

    /// <summary>
    ///     How many measured runs to enqueue, 1..<see cref="BenchmarkRunFreezeService.MaxRepeatCount" />. Everything but
    ///     the seed is frozen ONCE and the repeats share it.
    /// </summary>
    public int RepeatCount { get; init; } = 1;

    /// <summary>
    ///     Prepends one more run at repeat index 0, flagged <c>IsWarmup</c>: never ranked, never counted in a group's
    ///     statistics. It exists to absorb the first-launch costs (page cache cold, GPU clocks low) the measured repeats
    ///     should not pay.
    /// </summary>
    public bool Warmup { get; init; }

    /// <summary>
    ///     What the group measures. <see cref="BenchmarkRepeatMode.Throughput" /> is the default and the historical
    ///     behaviour: temperature 0, one fixed seed, so the answer is identical across repeats and only the machine varies.
    /// </summary>
    public BenchmarkRepeatMode RepeatMode { get; init; }

    /// <summary>
    ///     The temperature an <see cref="BenchmarkRepeatMode.AnswerVariance" /> group samples at, or
    ///     <see langword="null" /> for <see cref="BenchmarkRunFreezeService.DefaultAnswerVarianceTemperature" />. Ignored
    ///     in throughput mode, which is deterministic by definition.
    /// </summary>
    public double? AnswerVarianceTemperature { get; init; }
}

/// <summary>One model's freeze, decided but NOT written.</summary>
/// <remarks>
///     Every read a freeze takes — the verified model lease, the eligibility, the agent resolution, the project
///     version — has already happened and produced these commands;
///     <see cref="IBenchmarkRunFreezeService.CommitAsync" /> is the only step that touches the database. That split is
///     what lets a PAIR be validated on both sides before either side exists: committing one side first left the
///     caller with queued runs it was never told the ids of, and the only retry available duplicated that side.
/// </remarks>
public sealed class BenchmarkFrozenRunPlan
{
    public required Guid ProjectId { get; init; }

    public required long ExpectedProjectVersion { get; init; }

    public required IReadOnlyList<BenchmarkStartRunCommand> Commands { get; init; }
}

public sealed class BenchmarkRunBatchRequest
{
    public required Guid ProjectId { get; init; }

    public required long ExpectedProjectVersion { get; init; }

    public required IReadOnlyList<BenchmarkRunBatchItem> Items { get; init; }

    public required int RepeatCount { get; init; }

    public required bool Warmup { get; init; }

    public required BenchmarkRepeatMode RepeatMode { get; init; }

    public required double? AnswerVarianceTemperature { get; init; }
}

public sealed class BenchmarkRunBatchItem
{
    public required string ModelName { get; init; }

    public required string? KvCacheType { get; init; }
}

public sealed class BenchmarkRunBatchStartedItem
{
    public required string ModelName { get; init; }

    public required string? KvCacheType { get; init; }

    public required IReadOnlyList<Guid> RunIds { get; init; }
}

public enum BenchmarkRunBatchRejectionKind
{
    Failure,
    NotAttempted,
    TimeBudget
}

public sealed class BenchmarkRunBatchRejectedItem
{
    public required string ModelName { get; init; }

    public required string? KvCacheType { get; init; }

    public required BenchmarkRunBatchRejectionKind Kind { get; init; }

    public required string Message { get; init; }

    public Exception? Failure { get; init; }
}

public sealed class BenchmarkRunBatchResult
{
    public required long ProjectVersion { get; init; }

    public required IReadOnlyList<BenchmarkRunBatchStartedItem> Started { get; init; }

    public required IReadOnlyList<BenchmarkRunBatchRejectedItem> Rejected { get; init; }
}

/// <summary>
///     One task item as an operator writes it. The index, revision and input hash are absent on purpose: a caller that
///     could name them could present an answer to an old question as an answer to the current one.
/// </summary>
public sealed class BenchmarkTaskItemDraft
{
    public required string Prompt { get; init; }

    public string? Kind { get; init; }

    public string? ReferenceAnswer { get; init; }

    /// <summary>
    ///     Per-criterion overrides of the judge policy's verifier config, keyed by criterion id. Carried opaquely here —
    ///     it can hold expected answers, which is why it is stored encrypted.
    /// </summary>
    public JsonElement? VerifierConfig { get; init; }

    /// <summary>The parameters a generator item expands into child cases. Null for a plain prompt.</summary>
    public JsonElement? GeneratorConfig { get; init; }

    public bool CountsTowardScore { get; init; } = true;
}

public sealed class BenchmarkJudgePolicyChange
{
    public required BenchmarkProjectRecord Project { get; init; }

    /// <summary>The runs a judging was queued for, in the order they were enqueued. Empty on a no-op.</summary>
    public required IReadOnlyList<Guid> EnqueuedRunIds { get; init; }

    public required int? CohortGeneration { get; init; }
}
