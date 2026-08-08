namespace XE_Local_AI_Engine.Client.Services.Memory;

/// <summary>
///     Background dispatch for post-run adaptive memory. Both chat front doors (send + regenerate) call
///     <see cref="Dispatch" /> once, immediately after a Completed/Failed terminal is persisted, when the resolved agent
///     has the playbook enabled. The work is fire-and-forget: it runs on its OWN DI scope and DbContext with a FRESH
///     cancellation token (never the send token), so awaiting the model never delays the terminal SSE event, a
///     cancel-after-completion never loses a completed run's memory, and the request scope being disposed cannot fault
///     the extraction with an <see cref="ObjectDisposedException" />. All failures are swallowed text-free; the run path
///     is never affected.
/// </summary>
public interface IMemoryExtractionDispatcher
{
    /// <summary>
    ///     Schedules execution-log persistence plus memory extraction for the just-terminalized run, on a background
    ///     scope. Returns immediately; never throws into the caller. The <paramref name="telemetry" /> is metadata only
    ///     (no message content); the <paramref name="run" /> carries the conversation content needed for the node-local
    ///     model call and dedup, held only in the background scope.
    /// </summary>
    void Dispatch(MemoryExtractionDispatchContext telemetry, MemoryExtractionRunInput run);
}

/// <summary>
///     Metadata-only telemetry for the <c>AgentExecutionLog</c> row written alongside extraction. NEVER carries message
///     content. <see cref="ErrorClass" /> is an exception TYPE NAME only (never message text). Tokens are nullable —
///     streaming usage is best-effort and a GGUF model may omit it.
/// </summary>
public sealed record MemoryExtractionDispatchContext(
    Guid AgentDefinitionId,
    Guid ConversationId,
    Guid MessageId,
    string ModelName,
    string ConfigHash,
    long LatencyMs,
    bool Success,
    int? PromptTokens,
    int? CompletionTokens,
    string? ErrorClass);
