namespace XE_Local_AI_Engine.Client.Services.Events;

using System.Text;
using XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     Represents invocation state.
/// </summary>
public sealed class InvocationState
{
    // Streamed content/reasoning accumulate one chunk at a time on the hot streaming path. Backing the growth with a
    // StringBuilder keeps each append O(chunk) instead of the O(total) full-string reallocation a per-chunk
    // string.Concat forced. The materialized string is cached and only rebuilt when read after an append, so repeated
    // reads of an unchanged snapshot (the pump/resume registry read it several times) pay the ToString cost once.
    private StringBuilder? _streamedContentBuilder;
    private StringBuilder? _streamedThinkingContentBuilder;
    private string _streamedContent = string.Empty;
    private string _streamedThinkingContent = string.Empty;
    private bool _streamedContentDirty;
    private bool _streamedThinkingContentDirty;

    public Guid InvocationId { get; init; }

    public Guid ConversationId { get; init; }

    public InvocationStatus Status { get; set; }

    public string StreamedContent
    {
        get
        {
            if (_streamedContentDirty)
            {
                _streamedContent = _streamedContentBuilder?.ToString() ?? string.Empty;
                _streamedContentDirty = false;
            }

            return _streamedContent;
        }

        set
        {
            _streamedContent = value ?? string.Empty;
            _streamedContentBuilder = null;
            _streamedContentDirty = false;
        }
    }

    public int StreamedChunkCount { get; set; }

    public string StreamedThinkingContent
    {
        get
        {
            if (_streamedThinkingContentDirty)
            {
                _streamedThinkingContent = _streamedThinkingContentBuilder?.ToString() ?? string.Empty;
                _streamedThinkingContentDirty = false;
            }

            return _streamedThinkingContent;
        }

        set
        {
            _streamedThinkingContent = value ?? string.Empty;
            _streamedThinkingContentBuilder = null;
            _streamedThinkingContentDirty = false;
        }
    }

    public int StreamedThinkingChunkCount { get; set; }

    /// <summary>Appends a streamed content chunk without reallocating the whole accumulated string.</summary>
    public void AppendStreamedContent(string chunk)
    {
        _streamedContentBuilder ??= new StringBuilder(_streamedContent);
        _streamedContentBuilder.Append(chunk);
        _streamedContentDirty = true;
    }

    /// <summary>Appends a streamed reasoning chunk without reallocating the whole accumulated string.</summary>
    public void AppendStreamedThinkingContent(string chunk)
    {
        _streamedThinkingContentBuilder ??= new StringBuilder(_streamedThinkingContent);
        _streamedThinkingContentBuilder.Append(chunk);
        _streamedThinkingContentDirty = true;
    }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastUpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }

    public FailureCategory? FailureCategory { get; set; }

    public string? ModelUsed { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int? TotalTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    /// <summary>
    ///     Wall-clock generation duration in milliseconds, measured by the invocation runner across the whole turn
    ///     (prompt-eval through final token). Null until the invocation completes and for legacy/platform turns that
    ///     did not report it. Drives the optional tokens-per-second attribution.
    /// </summary>
    public long? GenerationDurationMs { get; set; }

    public InvocationApprovalState? PendingApproval { get; set; }

    public InvocationApprovalResolutionState? LastApprovalResolution { get; set; }

    public IReadOnlyList<InvocationToolCallState> PendingToolCalls { get; set; } = [];

    public InvocationToolCallResultState? LastToolCallResult { get; set; }
}
