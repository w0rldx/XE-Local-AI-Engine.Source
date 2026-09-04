namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Text;

/// <summary>
///     Accumulates the ordered interleave of reasoning segments and tool cards for a single assistant turn so the
///     terminal persist can write a <see cref="NodeChatMessagePart" /> list (the render source of truth on reload).
///     The local front doors (<c>NodeChatStreamService</c> / <c>NodeChatRegenerationService</c>) are the only place
///     that observes BOTH producers of the turn — the reasoning deltas fanned out by the persistence pump and the
///     tool-call lifecycle events — so accumulation lives here, fed by both handlers under one lock.
///     Ordering model (Option A): reasoning deltas extend the trailing reasoning segment; a tool event between two
///     reasoning runs closes the current reasoning segment, so a turn can show more than one Thoughts block. Tool
///     calls collapse requested-&gt;completed by tool-call id (the completed phase fills the result), guarding the
///     duplicate-tool-part class of bug. Each part is stamped with the shared monotonic stream sequence when it is
///     opened, preserving global order even though the two producers run concurrently.
/// </summary>
public sealed class NodeChatPartAccumulator
{
    private readonly List<MutablePart> _parts = [];
    private readonly Lock _syncRoot = new();
    private readonly Dictionary<string, MutablePart> _toolPartsByCallId = new(StringComparer.Ordinal);

    /// <summary>
    ///     Whether any part was accumulated. The caller persists an empty interleave only when the turn produced no
    ///     parts at all (a plain-text answer) by passing the snapshot regardless; this lets it skip the call cheaply.
    /// </summary>
    public bool HasParts
    {
        get
        {
            lock (_syncRoot)
            {
                return _parts.Count > 0;
            }
        }
    }

    /// <summary>
    ///     Appends a reasoning delta. Extends the trailing reasoning segment, or opens a new one when the turn has no
    ///     trailing reasoning segment yet (start of turn, or the previous part is a tool — the Option A boundary).
    /// </summary>
    public void AppendReasoning(string? delta, long sequence)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        lock (_syncRoot)
        {
            var trailing = _parts.Count > 0 ? _parts[^1] : null;
            if (trailing is { Kind: NodeChatMessagePartKinds.Reasoning })
            {
                trailing.AppendText(delta);
                return;
            }

            // Parts are appended in insertion order (concurrent feed), but Snapshot() reconciles global order via
            // OrderBy(Sequence), so the "preserves global order" guarantee holds even under concurrent producers.
            var part = new MutablePart(NodeChatMessagePartKinds.Reasoning, sequence);
            part.AppendText(delta);
            _parts.Add(part);
        }
    }

    /// <summary>
    ///     Records a tool call entering the requested phase: a new tool part in <c>waiting</c> state keyed by
    ///     <paramref name="toolCallId" />. A duplicate requested phase for the same id is ignored (idempotent).
    /// </summary>
    public void AppendToolRequested(string toolCallId, string toolName, string? args, bool requiresApproval, long sequence)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        lock (_syncRoot)
        {
            if (_toolPartsByCallId.ContainsKey(toolCallId))
            {
                return;
            }

            var part = new MutablePart(NodeChatMessagePartKinds.Tool, sequence)
            {
                ToolCallId = toolCallId,
                Name = toolName,
                State = NodeChatToolPartStates.Waiting,
                Args = args,
                RequiresApproval = requiresApproval
            };
            _parts.Add(part);
            _toolPartsByCallId[toolCallId] = part;
        }
    }

    /// <summary>
    ///     Collapses a tool call's completed phase into its existing requested part (received / failed plus result).
    ///     When no requested part was seen (defensive: a result without a prior call), a completed tool part is added.
    /// </summary>
    public void CompleteToolCall(string toolCallId, string toolName, string? result, bool isError, long sequence)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        lock (_syncRoot)
        {
            var terminalState = isError ? NodeChatToolPartStates.Failed : NodeChatToolPartStates.Received;
            if (_toolPartsByCallId.TryGetValue(toolCallId, out var existing))
            {
                existing.State = terminalState;
                existing.Result = result;
                return;
            }

            var part = new MutablePart(NodeChatMessagePartKinds.Tool, sequence)
            {
                ToolCallId = toolCallId,
                Name = toolName,
                State = terminalState,
                Result = result
            };
            _parts.Add(part);
            _toolPartsByCallId[toolCallId] = part;
        }
    }

    /// <summary>
    ///     Appends a non-fatal turn notice as its own part (kind <see cref="NodeChatMessagePartKinds.Notice" />,
    ///     unconditionally — notices are fire-once events, not a requested/completed pair, so there is nothing to
    ///     collapse by id). <paramref name="kind" /> is the <c>TurnNoticeKind</c> enum name, stored in
    ///     <see cref="NodeChatMessagePart.Name" />; <paramref name="message" /> is the sanitized text, stored in
    ///     <see cref="NodeChatMessagePart.Text" />; <paramref name="detail" /> is the notice's optional sanitized
    ///     structured detail (the dispatch reason code, the effective model name), stored in
    ///     <see cref="NodeChatMessagePart.State" />.
    ///     <para>
    ///         The part record's generic members carry a per-KIND meaning — a notice part already stores the notice
    ///         kind in <c>Name</c>, which holds a tool name on a tool part — and <c>State</c> is the free member a
    ///         notice part has never used. Reusing it keeps the persisted <c>metadata_json</c> blob's shape, and
    ///         therefore the wire schema, exactly as it is: a reloaded turn renders the detail its live stream showed
    ///         without a new field on any contract.
    ///     </para>
    /// </summary>
    public void AppendNotice(string kind, string message, long sequence, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(message);

        lock (_syncRoot)
        {
            var part = new MutablePart(NodeChatMessagePartKinds.Notice, sequence)
            {
                Name = kind,
                State = string.IsNullOrWhiteSpace(detail) ? null : detail
            };
            part.AppendText(message);
            _parts.Add(part);
        }
    }

    /// <summary>
    ///     Returns an immutable snapshot of the accumulated parts ordered by their opening sequence. Safe to call from
    ///     the terminal persist while producers may still race; the lock and the copy make it a consistent view.
    /// </summary>
    public IReadOnlyList<NodeChatMessagePart> Snapshot()
    {
        lock (_syncRoot)
        {
            return _parts
                   .OrderBy(static part => part.Sequence)
                   .Select(static part => part.ToPart())
                   .ToList();
        }
    }

    private sealed class MutablePart(string kind, long sequence)
    {
        // Reasoning deltas append delta-by-delta; a StringBuilder keeps the whole segment O(n) to build instead of
        // the O(n^2) a repeated string concat costs. Tool parts never append text, so the builder stays null for them
        // and Text materializes once at Snapshot().
        private StringBuilder? _text;

        public string Kind { get; } = kind;

        public long Sequence { get; } = sequence;

        public string? ToolCallId { get; init; }

        public string? Name { get; init; }

        public string? State { get; set; }

        public string? Args { get; init; }

        public string? Result { get; set; }

        public bool? RequiresApproval { get; init; }

        public void AppendText(string delta)
        {
            (_text ??= new StringBuilder()).Append(delta);
        }

        public NodeChatMessagePart ToPart()
        {
            // Sequence is bounded by the per-turn stream counter (int range in practice); cast keeps the wire shape int.
            return new NodeChatMessagePart(Kind,
                (int)Sequence,
                _text?.ToString(),
                ToolCallId,
                Name,
                State,
                Args,
                Result,
                RequiresApproval);
        }
    }
}
