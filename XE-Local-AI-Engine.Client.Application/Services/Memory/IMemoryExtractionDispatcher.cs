namespace XE_Local_AI_Engine.Client.Services.Memory;

/// <summary>
///     Background dispatch for post-run adaptive memory: both chat front doors call <see cref="Dispatch" /> once,
///     right after a terminal is persisted, when the resolved agent has the playbook enabled.
/// </summary>
/// <remarks>
///     The work is fire-and-forget on its OWN DI scope and DbContext with a FRESH cancellation token, never the send
///     token, so the model never delays the terminal SSE event, a cancel-after-completion never loses a completed
///     run's memory, and a disposed request scope cannot fault extraction with an
///     <see cref="ObjectDisposedException" />. Every failure is swallowed text-free; the run path is unaffected.
/// </remarks>
public interface IMemoryExtractionDispatcher
{
    /// <summary>
    ///     Schedules execution-log persistence plus memory extraction for the just-terminalized run, on a background
    ///     scope. It returns immediately and never throws into the caller.
    /// </summary>
    /// <param name="telemetry">Metadata only; it carries no message content.</param>
    /// <param name="run">
    ///     The conversation content the node-local model call and dedup need, held only in the background scope.
    /// </param>
    void Dispatch(MemoryExtractionDispatchContext telemetry, MemoryExtractionRunInput run);
}

/// <summary>
///     Metadata-only telemetry for the <c>AgentExecutionLog</c> row written alongside extraction, which NEVER carries
///     message content.
/// </summary>
/// <remarks>
///     <see cref="ErrorClass" /> is an exception TYPE NAME only, never message text, and tokens are nullable because
///     streaming usage is best-effort and a GGUF model may omit it.
/// </remarks>
public sealed class MemoryExtractionDispatchContext
{
    public required Guid AgentDefinitionId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required string ModelName { get; init; }

    public required string ConfigHash { get; init; }

    public required long LatencyMs { get; init; }

    public required bool Success { get; init; }

    public required int? PromptTokens { get; init; }

    public required int? CompletionTokens { get; init; }

    public required string? ErrorClass { get; init; }
}
