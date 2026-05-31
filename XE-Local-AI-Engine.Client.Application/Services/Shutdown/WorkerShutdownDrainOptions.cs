namespace XE_Local_AI_Engine.Client.Services.Shutdown;

/// <summary>
///     Configuration options for worker shutdown drain behavior.
/// </summary>
public sealed class WorkerShutdownDrainOptions
{
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan DrainTimeout { get; init; } = DefaultDrainTimeout;

    public static IReadOnlyList<string> DrainSequence { get; } =
    [
        "stop-accepting-remote-invocations",
        "await-active-invocations",
        "flush-dead-letter-outbox",
        "disconnect-worker-hub"
    ];
}
