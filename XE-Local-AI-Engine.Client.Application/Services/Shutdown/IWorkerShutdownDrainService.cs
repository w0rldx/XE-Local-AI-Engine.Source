namespace XE_Local_AI_Engine.Client.Services.Shutdown;

/// <summary>
///     Application service for i worker shutdown drain behavior.
/// </summary>
public interface IWorkerShutdownDrainService
{
    Task<WorkerShutdownDrainResult> DrainAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Value object carrying worker shutdown drain result data.
/// </summary>
public sealed record WorkerShutdownDrainResult(
    bool StopAcceptingRemoteInvocationsCompleted,
    bool ActiveInvocationsDrained,
    bool DeadLetterFlushCompleted,
    bool WorkerHubDisconnected,
    TimeSpan Elapsed,
    IReadOnlyList<string> Diagnostics)
{
    public bool Succeeded =>
        StopAcceptingRemoteInvocationsCompleted &&
        ActiveInvocationsDrained &&
        DeadLetterFlushCompleted &&
        WorkerHubDisconnected;
}
