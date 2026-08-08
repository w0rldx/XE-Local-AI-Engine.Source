namespace XE_Local_AI_Engine.Client.Services.Shutdown;

public interface IWorkerShutdownDrainService
{
    Task<WorkerShutdownDrainResult> DrainAsync(CancellationToken cancellationToken = default);
}

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
