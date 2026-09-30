namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     What one agent turn came to. It is a RESULT and not a row: the lane produces it off the tick, and the poll is
///     the only thing that turns it into a status.
/// </summary>
/// <remarks>
///     It carries a failure class rather than an exception because the task body catches everything — a turn that
///     faulted would leave the poll rethrowing on every tick forever, about work that is long over.
/// </remarks>
internal sealed record GraphWorkflowAgentTurn
{
    public required bool Succeeded { get; init; }

    public required GraphWorkflowFailureClass FailureClass { get; init; }

    public required string? SanitizedReason { get; init; }

    public required string Text { get; init; }

    public required JsonElement? Json { get; init; }

    public required GraphWorkflowAgentUsage? Usage { get; init; }

    /// <summary>The node's <c>output</c> when its kind shapes one of its own (a <c>DecisionModel</c>); null means the Agent shape.</summary>
    public JsonElement? Output { get; init; }

    /// <summary>The names of the attachments this turn went without because they left the conversation; null when none did.</summary>
    public IReadOnlyList<string>? AttachmentsSkipped { get; init; }
}
