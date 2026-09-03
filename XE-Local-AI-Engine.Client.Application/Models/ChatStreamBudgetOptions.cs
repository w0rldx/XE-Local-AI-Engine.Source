namespace XE_Local_AI_Engine.Client.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     How much of one streaming turn may be buffered or deferred. Bound from the <c>Chat:StreamBudget</c> section.
///     One record rather than several split by concern: every knob here answers the same question, they are tuned
///     together, and one section is one thing to find.
///     <para>
///         The emit cadence and the persistence cadence are deliberately DECOUPLED. The SSE emit debounce keeps live
///         frames at ~25/s so the UI stays fluid; the partial-flush knobs let the database write lag far behind that,
///         because a flush rewrites the whole accumulated message and a fixed 100 ms cadence therefore made per-turn
///         write volume quadratic in output length. Bounding a flush's delta at a FRACTION of what is already
///         persisted bounds the rewrite-to-append ratio regardless of message length.
///     </para>
///     <para>
///         The operator-editable disconnect grace (<c>DetachedGraceSeconds</c>) deliberately does NOT live here — it is
///         surfaced in the node-settings UI, so it is a stored node setting read through <c>INodeRuntimeSettings</c>.
///         Everything in this record is appsettings-only tuning.
///     </para>
/// </summary>
public sealed class ChatStreamBudgetOptions
{
    public const string SectionName = "Chat:StreamBudget";

    /// <summary>
    ///     Minimum spacing between live <c>assistant-delta</c> frames, which caps them at ~25/s independent of token
    ///     rate. Coalescing here — at the single producer, before a sequence number is minted — is what lets a
    ///     coalesced delta consume exactly one sequence, so the client's ordering guard never waits on a hole.
    /// </summary>
    [Range(0, 1000)]
    public int EmitDebounceMs { get; set; } = 40;

    /// <summary>
    ///     Maximum events buffered per stream — the live send/regenerate sink and each resume subscriber alike. At the
    ///     emit cadence above this is ~80 s of consumer lag before the queue overflows; a consumer that far behind
    ///     should resynchronize rather than replay 80 s of history, which is what the overflow reconcile makes it do.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int QueueCapacity { get; set; } = 2048;

    /// <summary>
    ///     Maximum characters buffered per stream. A count cap alone is not a memory bound: one tool result can be
    ///     megabytes on its own, so the queue is capped on both axes and overflows on whichever it reaches first.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxQueuedChars { get; set; } = 1_048_576;

    /// <summary>
    ///     Concurrent resume consumers per invocation. The cap REJECTS the excess subscriber rather than evicting an
    ///     existing one, so a runaway reconnect loop can never knock a working browser off its own stream.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxSubscribersPerInvocation { get; set; } = 4;

    /// <summary>
    ///     Above this much accumulated content + reasoning, a resume emits <c>assistant-reconcile</c> instead of the
    ///     opening <c>assistant-snapshot</c> and ends the stream — the client refetches the persisted conversation,
    ///     which holds the same text, for one request. Deliberately not a truncated snapshot: truncating would invent a
    ///     partial-replacement semantic the protocol does not have.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxReplaySnapshotChars { get; set; } = 1_048_576;

    /// <summary>
    ///     Floor on the partial-flush cadence: never persist more often than this, whatever the growth. Matches the
    ///     fixed cadence this policy replaced, so a slow, small message writes no more often than it used to.
    /// </summary>
    [Range(1, 60_000)]
    public int PartialFlushMinIntervalMs { get; set; } = 100;

    /// <summary>
    ///     Ceiling on the partial-flush cadence: a stream too slow to trip the growth trigger still checkpoints this
    ///     often. This is also the crash-loss bound — a hard kill loses at most this much of the in-flight turn.
    /// </summary>
    [Range(1, 60_000)]
    public int PartialFlushMaxIntervalMs { get; set; } = 2000;

    /// <summary>
    ///     Flush once the unpersisted tail reaches this fraction of what is already persisted. At 0.20 the total bytes
    ///     rewritten across a turn of <c>n</c> characters is bounded at ~6n — linear, where a fixed cadence was
    ///     quadratic.
    /// </summary>
    [Range(0.01, 1.0)]
    public double PartialFlushGrowthFraction { get; set; } = 0.20;

    /// <summary>
    ///     Floor under the growth trigger, so a short message does not flush on every few characters (20% of 40
    ///     characters is 8).
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PartialFlushMinGrowthChars { get; set; } = 512;
}
