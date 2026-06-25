namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="IMemoryExtractionDispatcher" />. Singleton: it owns the <see cref="IServiceScopeFactory" /> and
///     spins each post-run job onto its OWN async DI scope (fresh <c>NodeChatDbContext</c> + scoped stores/services) with
///     a FRESH cancellation token, mirroring the scheduler dispatch executor's per-run-scope pattern. Fire-and-forget:
///     <see cref="Dispatch" /> returns immediately and never throws into the chat path; the background job's catch-all
///     logs text-free (mirroring the embedding ranker's fallback logging) so no conversation content can leak through a
///     log, and a genuine cancellation on the fresh token is allowed to pass.
/// </summary>
internal sealed class MemoryExtractionDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<MemoryExtractionDispatcher> logger) : IMemoryExtractionDispatcher
{
    private readonly ILogger<MemoryExtractionDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public void Dispatch(MemoryExtractionDispatchContext telemetry, MemoryExtractionRunInput run)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(run);

        // Fire-and-forget on a detached task. We deliberately do NOT await or observe this from the chat path; the
        // background job owns its own scope, DbContext, and cancellation, so the terminal SSE event is never delayed and
        // a client disconnect/cancel after completion never loses a completed run's memory.
        _ = Task.Run(() => RunBackgroundAsync(telemetry, run));
    }

    private async Task RunBackgroundAsync(MemoryExtractionDispatchContext telemetry, MemoryExtractionRunInput run)
    {
        // Fresh token: a cancellation of the original send must not lose a completed run's memory. The background job is
        // bounded by its own work, not the client connection.
        var cancellationToken = CancellationToken.None;

        try
        {
            // Own scope + own DbContext: the request/pump scope that produced the terminal may already be disposed, so
            // resolving the scoped stores/services from a fresh scope avoids an ObjectDisposedException on the context.
            await using var scope = _scopeFactory.CreateAsyncScope();

            // Execution-log row FIRST (metadata only — no message content): it is the diagnostic record of the run and
            // must be written even if extraction is a no-op (temp chat / no model / no lesson).
            await WriteExecutionLogAsync(scope.ServiceProvider, telemetry, cancellationToken).ConfigureAwait(false);

            var extractionService = scope.ServiceProvider.GetRequiredService<IMemoryExtractionService>();
            _ = await extractionService.ExtractAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine cancellation on our own token — let it pass without an error log (there is no caller to surface to).
        }
        catch (Exception exception)
        {
            // Catch-all: a background memory job must NEVER affect the run path, and must NEVER log conversation content.
            // Log the exception TYPE NAME only — never the exception object, whose Message/stack could carry conversation
            // text or model output from the extraction round-trip (same text-free discipline as the exec-log ErrorClass
            // field and the embedding ranker's fallback logging).
            _logger.LogWarning("Background memory extraction failed ({ErrorClass}) for agent {AgentId}; the chat run is unaffected.",
                exception.GetType().Name,
                telemetry.AgentDefinitionId);
        }
    }

    private static async Task WriteExecutionLogAsync(IServiceProvider serviceProvider,
        MemoryExtractionDispatchContext telemetry,
        CancellationToken cancellationToken)
    {
        var executionLogStore = serviceProvider.GetRequiredService<IAgentExecutionLogStore>();

        _ = await executionLogStore.AddAsync(new AgentExecutionLogInput(telemetry.AgentDefinitionId,
                telemetry.ConversationId,
                telemetry.MessageId,
                telemetry.ModelName,
                telemetry.ConfigHash,
                telemetry.LatencyMs,
                telemetry.Success,
                telemetry.PromptTokens,
                telemetry.CompletionTokens,
                telemetry.ErrorClass),
            cancellationToken).ConfigureAwait(false);
    }
}
