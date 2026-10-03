namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Text;

/// <summary>
///     Accumulates the ordered interleave of reasoning segments and tool cards for one assistant turn, so the
///     terminal persist can write the <see cref="NodeChatMessagePart" /> list that renders on reload.
/// </summary>
/// <remarks>
///     The local front doors are the only place observing BOTH producers — the pump's reasoning deltas and the
///     tool-call lifecycle — so accumulation lives here, fed by both handlers under one lock. Reasoning deltas extend
///     the trailing segment and a tool event between two reasoning runs closes it, so a turn can show more than one
///     Thoughts block. Tool calls collapse requested to completed by tool-call id, guarding the duplicate-part bug
///     class, and each part is stamped with the shared monotonic sequence when opened, preserving global order.
/// </remarks>
public sealed class NodeChatPartAccumulator
{
    private readonly List<MutablePart> _parts = [];
    internal const int MaxToolImagesPerMessage = 8;
    internal const int MaxToolImageBytesPerMessage = 4 * 1024 * 1024;
    private int _imageParts;
    private int _imageBytes;
    private readonly Lock _syncRoot = new();
    private readonly Dictionary<string, MutablePart> _toolPartsByCallId = new(StringComparer.Ordinal);

    // Where the answer starts in the streamed content: the offset of the last requested tool call that moved it.
    private int _textBoundary;

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
    /// <param name="contentOffset">
    ///     The streamed-content length when the call was requested; the interim text before it becomes a text part
    ///     ordered just ahead of this tool.
    /// </param>
    public void AppendToolRequested(string toolCallId, string toolName, string? args, bool requiresApproval, long sequence, int? contentOffset = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        lock (_syncRoot)
        {
            if (_toolPartsByCallId.ContainsKey(toolCallId))
            {
                return;
            }

            if (contentOffset > _textBoundary)
            {
                _parts.Add(new MutablePart(NodeChatMessagePartKinds.Text, sequence)
                {
                    ContentStart = _textBoundary,
                    ContentEnd = contentOffset.Value
                });
                _textBoundary = contentOffset.Value;
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
    ///     Appends an image a tool call returned as its own part keyed to that call: media type in
    ///     <see cref="NodeChatMessagePart.Name" />, <c>data:</c> URI in <see cref="NodeChatMessagePart.Text" />.
    /// </summary>
    /// <remarks>The same generic-member reuse as a notice, so the wire schema is unchanged. Never replayed to a model.</remarks>
    public bool AppendToolImage(string toolCallId, string mediaType, string dataUri, long sequence)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);

        lock (_syncRoot)
        {
            // A per-message ceiling on top of the per-image cap: many small images must not grow one metadata row without
            // bound (Codex review 2026-09-30). Beyond it the card keeps only the placeholder text.
            if (_imageParts >= MaxToolImagesPerMessage || _imageBytes + dataUri.Length > MaxToolImageBytesPerMessage)
            {
                return false;
            }

            _imageParts++;
            _imageBytes += dataUri.Length;
            var part = new MutablePart(NodeChatMessagePartKinds.Image, sequence)
            {
                ToolCallId = toolCallId,
                Name = mediaType
            };
            part.AppendText(dataUri);
            _parts.Add(part);
            return true;
        }
    }

    /// <summary>
    ///     Appends a non-fatal turn notice as its own part, unconditionally: a notice is a fire-once event, not a
    ///     requested-completed pair, so there is nothing to collapse by id.
    /// </summary>
    /// <param name="kind">The <c>TurnNoticeKind</c> name, stored in <see cref="NodeChatMessagePart.Name" />.</param>
    /// <param name="message">The sanitized text, stored in <see cref="NodeChatMessagePart.Text" />.</param>
    /// <param name="sequence">The shared stream sequence stamped on the part when it opens.</param>
    /// <param name="detail">Optional sanitized detail, stored in <see cref="NodeChatMessagePart.State" />.</param>
    /// <remarks>
    ///     The part record's generic members carry a per-KIND meaning, and <c>State</c> is the free member a notice
    ///     part has never used. Reusing it keeps the persisted <c>metadata_json</c> shape — and therefore the wire
    ///     schema — exactly as it is, so a reloaded turn renders the detail its live stream showed.
    /// </remarks>
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
    ///     Returns the turn's answer: the part of <paramref name="content" /> after the last requested tool call's
    ///     offset, or all of it when no call split it. The persisted <c>Content</c> and every reader of it see only this.
    /// </summary>
    /// <remarks>
    ///     A turn that ended right after a tool call has no answer text; it keeps the whole text as its content and
    ///     <see cref="Snapshot" /> emits no text part, exactly as before the split existed.
    /// </remarks>
    public string FinalSegment(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        lock (_syncRoot)
        {
            return FinalSegment(content, _textBoundary);
        }
    }

    /// <summary>
    ///     The answer after <paramref name="boundary" />, the last requested call's content offset, with the same
    ///     no-answer fallback. Shared with the resume replay so its terminal re-states what the pump persisted.
    /// </summary>
    public static string FinalSegment(string content, int boundary)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content[EffectiveBoundary(content, boundary)..];
    }

    // The offset the answer starts at in this content, or 0 (no split) when nothing but whitespace follows it.
    private static int EffectiveBoundary(string content, int boundary)
    {
        var clamped = Math.Min(boundary, content.Length);
        return clamped > 0 && string.IsNullOrWhiteSpace(content[clamped..]) ? 0 : clamped;
    }

    /// <summary>
    ///     Returns an immutable snapshot of the accumulated parts ordered by their opening sequence. Safe to call from
    ///     the terminal persist while producers may still race; the lock and the copy make it a consistent view.
    /// </summary>
    /// <param name="content">
    ///     The full streamed content the text parts are sliced from; without it, or with no answer after the last
    ///     call, none is emitted.
    /// </param>
    public IReadOnlyList<NodeChatMessagePart> Snapshot(string? content = null)
    {
        lock (_syncRoot)
        {
            var splits = content is not null && EffectiveBoundary(content, _textBoundary) > 0;
            var parts = new List<NodeChatMessagePart>(_parts.Count);
            foreach (var part in _parts.OrderBy(static part => part.Sequence))
            {
                if (part.Kind != NodeChatMessagePartKinds.Text)
                {
                    parts.Add(part.ToPart());
                    continue;
                }

                // Clamped: a faulted terminal slices the last-PERSISTED content, which may end before the offset.
                var text = !splits || content is null
                    ? null
                    : content[Math.Min(part.ContentStart, content.Length)..Math.Min(part.ContentEnd, content.Length)];
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(part.ToPart(text));
                }
            }

            return parts;
        }
    }

    private sealed class MutablePart
    {
        // Reasoning appends delta by delta, so a StringBuilder builds the segment in linear rather than quadratic time.
        // Tool parts never append text, so the builder stays null and Text materializes once at Snapshot().
        private StringBuilder? _text;

        public MutablePart(string kind, long sequence)
        {
            Kind = kind;
            Sequence = sequence;
        }

        public string Kind { get; }

        public long Sequence { get; }

        public string? ToolCallId { get; init; }

        public string? Name { get; init; }

        public string? State { get; set; }

        public string? Args { get; init; }

        public string? Result { get; set; }

        public bool? RequiresApproval { get; init; }

        // A text part's slice of the streamed content, materialized at Snapshot from the terminal's content.
        public int ContentStart { get; init; }

        public int ContentEnd { get; init; }

        public void AppendText(string delta)
        {
            (_text ??= new StringBuilder()).Append(delta);
        }

        public NodeChatMessagePart ToPart(string? text = null)
        {
            // Sequence is bounded by the per-turn stream counter (int range in practice); cast keeps the wire shape int.
            return new NodeChatMessagePart(Kind,
                (int)Sequence,
                text ?? _text?.ToString(),
                ToolCallId,
                Name,
                State,
                Args,
                Result,
                RequiresApproval);
        }
    }
}
