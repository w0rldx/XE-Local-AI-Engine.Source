namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;

/// <summary>
///     A live handoff orchestration run. Drives the underlying MAF <c>StreamingRun</c> and exposes its events as a
///     normalized <see cref="OrchestrationUpdate" /> stream; tool approvals surface as
///     <see cref="OrchestrationUpdateKind.ApprovalRequest" /> updates and are answered on the SAME held run via
///     <see cref="RespondToApprovalAsync" />. Disposing cancels and tears down the run.
/// </summary>
public interface IOrchestrationRunSession : IAsyncDisposable
{
    /// <summary>
    ///     Drains the run, yielding normalized updates until the workflow produces its terminal output (or fails, or
    ///     is cancelled). After an <see cref="OrchestrationUpdateKind.ApprovalRequest" /> the consumer must call
    ///     <see cref="RespondToApprovalAsync" /> and keep enumerating — the tool executes in a later superstep.
    /// </summary>
    IAsyncEnumerable<OrchestrationUpdate> WatchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Answers a pending tool approval keyed by <paramref name="requestId" /> (the value from the
    ///     <see cref="OrchestrationUpdateKind.ApprovalRequest" /> update). Sends the response into the held run;
    ///     <paramref name="approved" />=true lets the tool execute, =false synthesizes a not-approved result so the
    ///     tool never runs. The caller must continue draining <see cref="WatchAsync" /> afterwards.
    /// </summary>
    Task RespondToApprovalAsync(string requestId, bool approved, string? reason, CancellationToken cancellationToken = default);
}
