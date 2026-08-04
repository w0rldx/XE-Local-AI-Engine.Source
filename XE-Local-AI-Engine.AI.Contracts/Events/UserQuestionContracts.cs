namespace XE_Local_AI_Engine.AI.Contracts.Events;

/// <summary>
///     One option the agent offers for a <see cref="UserQuestionSpec" />. <see cref="Recommended" /> is advisory only —
///     it drives a badge in the chat card and never pre-commits the answer, so the operator always makes the choice.
/// </summary>
public sealed record UserQuestionOption(string Label, string? Description, bool Recommended);

/// <summary>
///     A single question the agent is asking the operator, as parsed from the <c>ask_user</c> tool call's arguments.
///     One tool call may carry several of these (answered as one form).
/// </summary>
/// <param name="Header">Short chip label for the question. May be empty.</param>
/// <param name="Question">The question text shown to the operator.</param>
/// <param name="MultiSelect">When true the operator may pick more than one option.</param>
/// <param name="Options">The offered options. Always at least two by schema; validated on parse.</param>
public sealed record UserQuestionSpec(string Header, string Question, bool MultiSelect, IReadOnlyList<UserQuestionOption> Options);

/// <summary>
///     The operator's answer to one <see cref="UserQuestionSpec" />. <see cref="Selected" /> carries the chosen option
///     labels; <see cref="Other" /> carries free text when the operator used the client-appended "Other" row. Both may
///     be populated (a multi-select answer plus free text); an empty <see cref="Selected" /> with a null
///     <see cref="Other" /> is rejected by the resolve endpoint's validator.
/// </summary>
public sealed record UserQuestionAnswer(string Question, IReadOnlyList<string> Selected, string? Other);

/// <summary>
///     Carries the operator's answers back into the waiting turn. The <see cref="RequestId" /> is the opaque per-question
///     key the runner registered and the browser echoed back — the same correlation contract
///     <see cref="ApprovalResolvedEvent" /> uses for a tool approval. Dispatching an unknown or already-resolved id is a
///     no-op, never a fault, so a duplicate or stale post can never disturb the turn.
/// </summary>
public sealed record UserQuestionAnsweredEvent(string RequestId, IReadOnlyList<UserQuestionAnswer> Answers);
