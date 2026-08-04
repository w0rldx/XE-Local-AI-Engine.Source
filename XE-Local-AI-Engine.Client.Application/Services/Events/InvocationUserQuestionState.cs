namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.AI.Contracts.Events;

/// <summary>
///     The live "a question is waiting on the operator" slot on <see cref="InvocationState" />. Mirrors
///     <see cref="InvocationApprovalState" />, with one deliberate difference: it carries the QUESTIONS as well as the
///     request id, because a client that reconnects mid-turn cannot render an answerable prompt from an id alone.
///     That is what makes the reconnect replay possible — see <c>InvocationResumeRegistry</c>.
/// </summary>
public sealed record InvocationUserQuestionState(
    string RequestId,
    string CallId,
    string ToolName,
    IReadOnlyList<UserQuestionSpec> Questions,
    DateTimeOffset RequestedAt);
