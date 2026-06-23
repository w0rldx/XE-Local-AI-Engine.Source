namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

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
    private readonly System.Threading.Lock _syncRoot = new();
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
                trailing.Text += delta;
                return;
            }

            // Parts are appended in insertion order (concurrent feed), but Snapshot() reconciles global order via
            // OrderBy(Sequence), so the "preserves global order" guarantee holds even under concurrent producers.
            _parts.Add(new MutablePart(NodeChatMessagePartKinds.Reasoning, sequence)
            {
                Text = delta
            });
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
        public string Kind { get; } = kind;

        public long Sequence { get; } = sequence;

        public string? Text { get; set; }

        public string? ToolCallId { get; init; }

        public string? Name { get; init; }

        public string? State { get; set; }

        public string? Args { get; init; }

        public string? Result { get; set; }

        public bool? RequiresApproval { get; init; }

        public NodeChatMessagePart ToPart()
        {
            // Sequence is bounded by the per-turn stream counter (int range in practice); cast keeps the wire shape int.
            return new NodeChatMessagePart(Kind,
                (int)Sequence,
                Text,
                ToolCallId,
                Name,
                State,
                Args,
                Result,
                RequiresApproval);
        }
    }
}
