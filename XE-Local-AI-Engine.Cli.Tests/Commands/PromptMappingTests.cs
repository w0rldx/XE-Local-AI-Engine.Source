namespace XE_Local_AI_Engine.Cli.Tests.Commands;

using XE_Local_AI_Engine.Cli.Commands;
using XE_Local_AI_Engine.Cli.Rendering;
using XE_Local_AI_Engine.Cli.Streaming;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>What the approval and question prompts offer and the resolve requests they turn a choice into.</summary>
[Category(TestCategories.Unit)]
public sealed class PromptMappingTests
{
    [Test]
    public void ApprovalChoices_OfferTheSessionScopeOnlyWhenTheNodeMarkedTheCallEligible()
    {
        AssertEx.Equal("ApproveOnce,ApproveForSession,Deny", string.Join(',', TurnPrompter.ApprovalChoices(Approval(sessionScopeEligible: true))));
        AssertEx.Equal("ApproveOnce,Deny", string.Join(',', TurnPrompter.ApprovalChoices(Approval(sessionScopeEligible: false))));
        AssertEx.Equal("ApproveOnce,Deny", string.Join(',', TurnPrompter.ApprovalChoices(Approval(sessionScopeEligible: null))));
    }

    [Test]
    [Arguments(nameof(ApprovalChoice.ApproveOnce), true, true, "Once")]
    [Arguments(nameof(ApprovalChoice.ApproveForSession), true, true, "Session")]
    [Arguments(nameof(ApprovalChoice.ApproveForSession), false, true, "Once")]
    [Arguments(nameof(ApprovalChoice.Deny), true, false, "Once")]
    public void ToApprovalRequest_MapsTheChoiceToTheResolveRequest(string choice, bool eligible, bool approved, string scope)
    {
        var request = TurnPrompter.ToApprovalRequest(Approval(eligible), Enum.Parse<ApprovalChoice>(choice));

        AssertEx.Equal("approval-7", request.RequestId);
        AssertEx.Equal(approved, request.Approved);
        AssertEx.Equal(scope, request.Scope);
    }

    [Test]
    [Arguments("""[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]""", 1)]
    [Arguments("""{"questions":[{"question":"Pick","options":[{"label":"A"}]},{"question":"Again","options":[{"label":"C"}]}]}""", 2)]
    [Arguments("""[{"question":"No options","options":[]},{"question":"Pick","options":[{"label":""},{"label":"A"}]}]""", 1)]
    [Arguments("""[{"question":"","options":[{"label":"A"}]}]""", 0)]
    [Arguments("not json", 0)]
    [Arguments("""{"other":[]}""", 0)]
    [Arguments("", 0)]
    public void Parse_AcceptsTheArrayAndTheEnvelopeAndDropsUnusableQuestions(string payload, int expected)
    {
        AssertEx.Equal(expected, UserQuestionParser.Parse(payload).Count);
    }

    [Test]
    public void Parse_ReadsHeaderMultiSelectAndOptionDetails_TreatingABlankHeaderAsAbsent()
    {
        var questions = UserQuestionParser.Parse("""
            [{"header":"Scope","question":"Which files?","multiSelect":true,
              "options":[{"label":"src","description":"the code","recommended":true},{"label":"docs"}]},
             {"header":"  ","question":"Proceed?","options":[{"label":"yes"}]}]
            """);

        AssertEx.Equal("Scope", questions[0].Header);
        AssertEx.True(questions[0].MultiSelect);
        AssertEx.Equal("the code", questions[0].Options[0].Description);
        AssertEx.True(questions[0].Options[0].Recommended);
        AssertEx.False(questions[0].Options[1].Recommended);
        AssertEx.Null(questions[1].Header);
        AssertEx.False(questions[1].MultiSelect);
    }

    [Test]
    public void ToAnswer_KeepsKnownLabelsCapsASingleSelectAtOneAndCarriesTheOtherText()
    {
        var questions = UserQuestionParser.Parse("""
            [{"question":"Many","multiSelect":true,"options":[{"label":"a"},{"label":"b"}]},
             {"question":"One","options":[{"label":"x"},{"label":"y"}]}]
            """);

        var many = UserQuestionParser.ToAnswer(questions[0], ["a", TurnPrompter.OtherChoice, "b", "a"], "  typed  ");
        var one = UserQuestionParser.ToAnswer(questions[1], ["y", "x"], other: null);
        var blankOther = UserQuestionParser.ToAnswer(questions[1], [TurnPrompter.OtherChoice], "   ");

        AssertEx.Equal("Many", many.Question);
        AssertEx.Equal("a,b", string.Join(',', many.Selected));
        AssertEx.Equal("typed", many.Other);
        AssertEx.Equal("y", string.Join(',', one.Selected));
        AssertEx.Null(one.Other);
        AssertEx.Empty(blankOther.Selected);
        AssertEx.Null(blankOther.Other);
    }

    [Test]
    [Arguments("/new  My title ", "/new", "My title")]
    [Arguments("/quit", "/quit", "")]
    [Arguments("  /agent Coder Bot", "/agent", "Coder Bot")]
    public void ParseSlash_SplitsTheCommandFromItsArgument(string line, string name, string argument)
    {
        var parsed = ChatCommand.ParseSlash(line);

        AssertEx.Equal(name, parsed.Name);
        AssertEx.Equal(argument, parsed.Argument);
    }

    [Test]
    [Arguments("Short question", "Short question")]
    [Arguments("First line\nsecond line", "First line")]
    [Arguments("one two three four five six seven eight nine ten eleven twelve thirteen", "one two three four five six seven eight nine ten eleven...")]
    public void DeriveTitle_UsesTheFirstLineCutAtAWord(string prompt, string expected)
    {
        AssertEx.Equal(expected, AskCommand.DeriveTitle(prompt));
    }

    private static CliStreamEvent Approval(bool? sessionScopeEligible) =>
        new()
        {
            Kind = CliStreamEventKind.ApprovalRequested,
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Source = new ChatStreamEvent
            {
                Type = ChatStreamEventTypes.ApprovalRequested,
                ApprovalRequestId = "approval-7",
                ToolName = "read_skill",
                SessionScopeEligible = sessionScopeEligible
            }
        };
}
