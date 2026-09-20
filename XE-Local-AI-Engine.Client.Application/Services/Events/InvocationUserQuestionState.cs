namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>The live "a question is waiting on the operator" slot on <see cref="InvocationState" />.</summary>
/// <remarks>
///     Mirrors <see cref="InvocationApprovalState" />, with one deliberate difference: it carries the QUESTIONS as well
///     as the request id, because a client that reconnects mid-turn cannot render an answerable prompt from an id
///     alone. That is what makes the reconnect replay possible — see <c>InvocationResumeRegistry</c>.
/// </remarks>
public sealed class InvocationUserQuestionState
{
    public required string RequestId { get; init; }

    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public required IReadOnlyList<UserQuestionSpec> Questions { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }
}
