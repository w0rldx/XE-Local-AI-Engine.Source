namespace XE_Local_AI_Engine.Cli.Rendering;

// The ask_user question shape, as the SPA's AskUserQuestionWire.ts reads it from ChatStreamEvent.Questions.

/// <summary>One selectable option; <see cref="Recommended" /> is advisory and never pre-selected.</summary>
internal sealed class UserQuestionOption
{
    public required string Label { get; init; }

    public string? Description { get; init; }

    public bool Recommended { get; init; }
}

/// <summary>One of the one to four questions an <c>ask_user</c> call carries.</summary>
internal sealed class UserQuestion
{
    public string? Header { get; init; }

    public required string Question { get; init; }

    public bool MultiSelect { get; init; }

    public required IReadOnlyList<UserQuestionOption> Options { get; init; }
}
