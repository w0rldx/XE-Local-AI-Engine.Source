namespace XE_Local_AI_Engine.AI.Contracts.Events;

/// <summary>
///     One option the agent offers for a <see cref="UserQuestionSpec" />. <see cref="Recommended" /> is advisory only —
///     it drives a badge in the chat card and never pre-commits the answer, so the operator always makes the choice.
/// </summary>
public sealed class UserQuestionOption
{
    public required string Label { get; init; }

    public required string? Description { get; init; }

    public required bool Recommended { get; init; }
}

/// <summary>
///     A single question the agent is asking the operator, as parsed from the <c>ask_user</c> tool call's arguments.
///     One tool call may carry several of these (answered as one form).
/// </summary>
public sealed class UserQuestionSpec
{
    /// <summary>Short chip label for the question. May be empty.</summary>
    public required string Header { get; init; }

    /// <summary>The question text shown to the operator.</summary>
    public required string Question { get; init; }

    /// <summary>When true the operator may pick more than one option.</summary>
    public required bool MultiSelect { get; init; }

    /// <summary>The offered options. Always at least two by schema; validated on parse.</summary>
    public required IReadOnlyList<UserQuestionOption> Options { get; init; }
}

/// <summary>The operator's answer to one <see cref="UserQuestionSpec" />.</summary>
/// <remarks>
///     <see cref="Selected" /> carries the chosen option labels and <see cref="Other" /> the free text from the
///     client-appended "Other" row; both may be populated, for a multi-select answer plus free text. An empty
///     <see cref="Selected" /> with a null <see cref="Other" /> is rejected by the resolve endpoint's validator.
/// </remarks>
public sealed class UserQuestionAnswer
{
    public required string Question { get; init; }

    public required IReadOnlyList<string> Selected { get; init; }

    public required string? Other { get; init; }
}

/// <summary>Carries the operator's answers back into the waiting turn.</summary>
/// <remarks>
///     <see cref="RequestId" /> is the opaque per-question key the runner registered and the browser echoed back — the
///     same correlation contract <see cref="ApprovalResolvedEvent" /> uses for a tool approval. Dispatching an unknown
///     or already-resolved id is a no-op, never a fault, so a duplicate or stale post cannot disturb the turn.
/// </remarks>
public sealed class UserQuestionAnsweredEvent
{
    public required string RequestId { get; init; }

    public required IReadOnlyList<UserQuestionAnswer> Answers { get; init; }
}
