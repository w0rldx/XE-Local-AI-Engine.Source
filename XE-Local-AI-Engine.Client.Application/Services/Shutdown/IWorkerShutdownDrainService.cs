namespace XE_Local_AI_Engine.Client.Services.Shutdown;

public interface IWorkerShutdownDrainService
{
    Task<WorkerShutdownDrainResult> DrainAsync(CancellationToken cancellationToken = default);
}

public sealed class WorkerShutdownDrainResult
{
    public required bool StopAcceptingRemoteInvocationsCompleted { get; init; }

    public required bool ActiveInvocationsDrained { get; init; }

    public required bool DeadLetterFlushCompleted { get; init; }

    public required bool WorkerHubDisconnected { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public required IReadOnlyList<string> Diagnostics { get; init; }

    public bool Succeeded =>
        StopAcceptingRemoteInvocationsCompleted &&
        ActiveInvocationsDrained &&
        DeadLetterFlushCompleted &&
        WorkerHubDisconnected;
}
