namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Text.Json;

/// <summary>
///     One event on an integration execution's stream, as an external caller sees it.
///     <para>
///         <see cref="Sequence" /> is monotonic per execution and starts at 1 with <c>execution.accepted</c>. Holes are
///         legal: a sequence reserved for a durable-before-visible event whose commit failed is abandoned, and a caller
///         treats <c>Last-Event-ID</c> as a watermark rather than a counter.
///     </para>
///     <para>
///         <see cref="ContentType" /> and <see cref="Payload" /> are populated only where the type defines them — the
///         tool's declared content type and its verbatim payload on an <c>external.output</c> event, a small structured
///         object on the others.
///     </para>
/// </summary>
public sealed record IntegrationStreamEvent(
    string Type,
    long Sequence,
    Guid ExecutionId,
    Guid SessionId,
    long OccurredAtUtc,
    string? ContentType,
    JsonElement? Payload);

/// <summary>The closed set of <see cref="IntegrationStreamEvent.Type" /> values, and which of them are persisted.</summary>
public static class IntegrationStreamEventTypes
{
    /// <summary>Admission committed. Always sequence 1, and always without a payload.</summary>
    public const string ExecutionAccepted = "execution.accepted";

    /// <summary>The execution is waiting for the node's single invocation lease. Emitted only when it actually waits.</summary>
    public const string ExecutionQueued = "execution.queued";

    /// <summary>The lease is held and the runner is about to be called.</summary>
    public const string ExecutionStarted = "execution.started";

    /// <summary>One chunk of assistant text. Streamed only — per-token noise never reaches the events table.</summary>
    public const string AssistantDelta = "assistant.delta";

    /// <summary>The full final assistant text, bounded. Streamed only: it already lands in the owned conversation.</summary>
    public const string AssistantCompleted = "assistant.completed";

    /// <summary>A tool call began. Payload carries the tool name only.</summary>
    public const string ToolStarted = "tool.started";

    /// <summary>A tool call finished. Payload carries the tool name and whether it succeeded.</summary>
    public const string ToolCompleted = "tool.completed";

    /// <summary>An <c>emit_output</c> call: the caller-facing payload, forwarded verbatim.</summary>
    public const string ExternalOutput = "external.output";

    /// <summary>The run finished normally.</summary>
    public const string ExecutionCompleted = "execution.completed";

    /// <summary>The run failed. Payload carries the closed failure category and a content-free summary.</summary>
    public const string ExecutionFailed = "execution.failed";

    /// <summary>The run was cancelled, before or during execution.</summary>
    public const string ExecutionCancelled = "execution.cancelled";

    /// <summary>
    ///     The subset written to <c>integration_execution_events</c>. Written as a literal allowlist of nine, never as
    ///     "all except": a twelfth type added later must not become persisted by default. Both assistant types are
    ///     deliberately out — <see cref="AssistantDelta" /> is per-token noise, and <see cref="AssistantCompleted" />
    ///     would duplicate the final text that already lands in the owned conversation as an assistant message. A wrong
    ///     entry here copies transcript content into the event table, which is the leak that table was designed to
    ///     avoid.
    /// </summary>
    public static readonly IReadOnlySet<string> Persisted = new HashSet<string>(StringComparer.Ordinal)
    {
        ExecutionAccepted,
        ExecutionQueued,
        ExecutionStarted,
        ToolStarted,
        ToolCompleted,
        ExternalOutput,
        ExecutionCompleted,
        ExecutionFailed,
        ExecutionCancelled
    };
}
