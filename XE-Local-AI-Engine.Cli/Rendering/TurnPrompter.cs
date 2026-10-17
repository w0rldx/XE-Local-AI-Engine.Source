namespace XE_Local_AI_Engine.Cli.Rendering;

using Spectre.Console;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Streaming;

/// <summary>Asks the person at the terminal to decide a parked approval or answer an <c>ask_user</c> question; never decides alone.</summary>
internal sealed class TurnPrompter
{
    internal const string OtherChoice = "Other (type an answer)";

    private readonly IAnsiConsole _console;

    public TurnPrompter(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    /// <summary>The choices offered: approve for the session only when the node marked the call eligible (read-only skill tools).</summary>
    public static IReadOnlyList<ApprovalChoice> ApprovalChoices(CliStreamEvent approval)
    {
        ArgumentNullException.ThrowIfNull(approval);

        return approval.Source.SessionScopeEligible == true
            ? [ApprovalChoice.ApproveOnce, ApprovalChoice.ApproveForSession, ApprovalChoice.Deny]
            : [ApprovalChoice.ApproveOnce, ApprovalChoice.Deny];
    }

    public static ResolveApprovalRequest ToApprovalRequest(CliStreamEvent approval, ApprovalChoice choice)
    {
        ArgumentNullException.ThrowIfNull(approval);

        return new ResolveApprovalRequest
        {
            RequestId = approval.Source.ApprovalRequestId ?? string.Empty,
            Approved = choice != ApprovalChoice.Deny,
            Scope = choice == ApprovalChoice.ApproveForSession && approval.Source.SessionScopeEligible == true ? "Session" : "Once"
        };
    }

    public static string Describe(ApprovalChoice choice) =>
        choice switch
        {
            ApprovalChoice.ApproveOnce => "Approve once",
            ApprovalChoice.ApproveForSession => "Approve for this session",
            _ => "Deny"
        };

    public async Task<ResolveApprovalRequest> DecideApprovalAsync(CliStreamEvent approval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);

        var prompt = new SelectionPrompt<ApprovalChoice>()
                     .Title($"Allow tool [bold]{Markup.Escape(approval.Source.ToolName ?? "unknown")}[/]?")
                     .UseConverter(Describe)
                     .AddChoices(ApprovalChoices(approval));
        var choice = await _console.PromptAsync(prompt, cancellationToken);
        return ToApprovalRequest(approval, choice);
    }

    /// <summary>Asks every question in turn; null when the payload holds no question this client can render.</summary>
    public async Task<ResolveQuestionRequest?> AnswerQuestionsAsync(CliStreamEvent question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);

        var questions = UserQuestionParser.Parse(question.Source.Questions);
        if (questions.Count == 0 || string.IsNullOrEmpty(question.Source.QuestionRequestId))
        {
            return null;
        }

        var answers = new List<QuestionAnswer>(questions.Count);
        foreach (var item in questions)
        {
            answers.Add(await AskAsync(item, cancellationToken));
        }

        return new ResolveQuestionRequest
        {
            RequestId = question.Source.QuestionRequestId,
            Answers = answers
        };
    }

    private static string Label(UserQuestionOption option) =>
        Markup.Escape(option.Label)
        + (option.Recommended ? " [green](recommended)[/]" : string.Empty)
        + (option.Description is { } description ? $" [grey]- {Markup.Escape(description)}[/]" : string.Empty);

    private async Task<QuestionAnswer> AskAsync(UserQuestion question, CancellationToken cancellationToken)
    {
        var title = (question.Header is { } header ? $"[bold]{Markup.Escape(header)}[/]: " : string.Empty) + Markup.Escape(question.Question);
        var labels = question.Options.Select(static option => option.Label).Append(OtherChoice).ToList();

        string Convert(string label) =>
            question.Options.FirstOrDefault(option => string.Equals(option.Label, label, StringComparison.Ordinal)) is { } option ? Label(option) : label;

        IReadOnlyList<string> picked = question.MultiSelect
            ? await _console.PromptAsync(new MultiSelectionPrompt<string>().Title(title).UseConverter(Convert).AddChoices(labels), cancellationToken)
            : [await _console.PromptAsync(new SelectionPrompt<string>().Title(title).UseConverter(Convert).AddChoices(labels), cancellationToken)];
        var other = picked.Contains(OtherChoice, StringComparer.Ordinal)
            ? await _console.PromptAsync(new TextPrompt<string>("Your answer:"), cancellationToken)
            : null;
        return UserQuestionParser.ToAnswer(question, picked, other);
    }
}

/// <summary>What the person decided for a parked tool call.</summary>
internal enum ApprovalChoice
{
    ApproveOnce,
    ApproveForSession,
    Deny
}
