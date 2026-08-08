namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Publishes Open Canvas (Preview) run events to the connected operator over SignalR. EVERY event carries the
///     <c>RunId</c> — that is the real cross-contamination guard when more than one run is in flight (a single hub
///     connection may drive several runs). The default implementation is a no-op
///     (<see cref="Implementation.NullPreviewWorkflowEventPublisher" />); the Client host swaps in a hub-backed
///     publisher.
///     Privacy note (documented exception): unlike the Scheduler ("sanitize everything"), these payloads carry
///     the operator's OWN transient run output (the Debug feature's whole point) over the localhost Operator hub.
///     Nothing here is persisted, logged, or indexed.
/// </summary>
public interface IPreviewWorkflowEventPublisher
{
    /// <summary>Publishes a node-scoped event (<c>preview.node.started|output|debug|completed|failed</c>).</summary>
    Task PublishNodeAsync(PreviewWorkflowNodeHubEvent nodeEvent, CancellationToken cancellationToken = default);

    /// <summary>Publishes a run-lifecycle event (<c>preview.run.started|paused|completed|failed|cancelled</c>).</summary>
    Task PublishRunAsync(PreviewWorkflowRunHubEvent runEvent, CancellationToken cancellationToken = default);
}

/// <summary>
///     Stable SignalR client-method names for preview events. These double as the wire event-type discriminator on the
///     payloads so the React client subscribes per event name (mirrors <c>SchedulerHubEvents</c>).
/// </summary>
public static class PreviewWorkflowHubEvents
{
    public const string NodeStarted = "preview.node.started";
    public const string NodeOutput = "preview.node.output";
    public const string NodeDebug = "preview.node.debug";
    public const string NodeCompleted = "preview.node.completed";
    public const string NodeFailed = "preview.node.failed";

    public const string RunStarted = "preview.run.started";
    public const string RunPaused = "preview.run.paused";
    public const string RunCompleted = "preview.run.completed";
    public const string RunFailed = "preview.run.failed";
    public const string RunCancelled = "preview.run.cancelled";
}

/// <summary>
///     Node-scoped run event. <see cref="RunId" /> is mandatory (scopes delivery / prevents cross-run contamination).
///     <see cref="Output" /> carries the operator's transient node/debug output (documented privacy exception);
///     <see cref="Error" /> a sanitized failure message. <see cref="Seq" /> is a per-run monotonically increasing
///     sequence shared across BOTH node and run events of the same run (one counter per run) so a late subscriber can
///     replay the buffer and dedupe any event delivered both via replay and live.
/// </summary>
public sealed record PreviewWorkflowNodeHubEvent(
    string EventType,
    Guid RunId,
    string NodeId,
    string? Output,
    string? Error,
    long OccurredAtUtc,
    long Seq);

/// <summary>
///     Run-lifecycle event. <see cref="RunId" /> is mandatory. <see cref="NodeId" /> is set only for the pause event
///     (the Pause node), <see cref="Output" /> for pause (upstream display) and completed (terminal output),
///     <see cref="RequestId" /> for pause (the resume token), <see cref="Error" /> for failed. <see cref="Seq" /> is a
///     per-run monotonically increasing sequence shared across BOTH node and run events of the same run (one counter
///     per run) so a late subscriber can replay the buffer and dedupe any event delivered both via replay and live.
/// </summary>
public sealed record PreviewWorkflowRunHubEvent(
    string EventType,
    Guid RunId,
    string? NodeId,
    string? Output,
    string? Error,
    string? RequestId,
    long OccurredAtUtc,
    long Seq);
