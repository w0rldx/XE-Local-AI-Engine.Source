namespace XE_Local_AI_Engine.Tests.Interaction;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class UserQuestionParserTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public void TryParse_WithAWellFormedCall_ProjectsEveryField()
    {
        var parsed = UserQuestionParser.TryParse(Arguments("""
            {
              "questions": [
                {
                  "header": " Auth method ",
                  "question": " Which auth method? ",
                  "multiSelect": true,
                  "options": [
                    { "label": " OAuth device flow ", "description": " Works headless. ", "recommended": true },
                    { "label": "API key" }
                  ]
                }
              ]
            }
            """), out var questions, out var error);

        AssertEx.True(parsed, error);
        var question = AssertEx.NotNull(questions).Single();
        AssertEx.Equal("Auth method", question.Header);
        AssertEx.Equal("Which auth method?", question.Question);
        AssertEx.True(question.MultiSelect);
        AssertEx.Equal(expected: 2, question.Options.Count);
        AssertEx.Equal("OAuth device flow", question.Options[0].Label);
        AssertEx.Equal("Works headless.", question.Options[0].Description);
        AssertEx.True(question.Options[0].Recommended);
        AssertEx.Null(question.Options[1].Description, "an omitted description must project as null, not as an empty string");
        AssertEx.False(question.Options[1].Recommended);
    }

    [Test]
    public void TryParse_WithOmittedOptionalFields_AppliesTheSchemaDefaults()
    {
        var parsed = UserQuestionParser.TryParse(Arguments("""
            {"questions":[{"question":"Ship it?","options":[{"label":"Yes"},{"label":"No"}]}]}
            """), out var questions, out var error);

        AssertEx.True(parsed, error);
        var question = AssertEx.NotNull(questions).Single();
        AssertEx.Equal(string.Empty, question.Header);
        AssertEx.False(question.MultiSelect, "multiSelect defaults to single choice");
    }

    [Test]
    public void TryParse_WithFourQuestions_IsAccepted()
    {
        var questionJson = """{"question":"Q?","options":[{"label":"A"},{"label":"B"}]}""";
        var parsed = UserQuestionParser.TryParse(Arguments($$"""{"questions":[{{questionJson}},{{questionJson}},{{questionJson}},{{questionJson}}]}"""),
            out var questions,
            out var error);

        AssertEx.True(parsed, error);
        AssertEx.Equal(expected: 4, AssertEx.NotNull(questions).Count);
    }

    [Test]
    [Arguments("""{"questions":[]}""", "an empty questions array")]
    [Arguments("""{"questions":null}""", "a null questions array")]
    [Arguments("""{"prompt":"hi"}""", "a call that omits questions entirely")]
    [Arguments("""{"questions":[{"question":"  ","options":[{"label":"A"},{"label":"B"}]}]}""", "blank question text")]
    [Arguments("""{"questions":[{"question":"Q?","options":[{"label":"A"}]}]}""", "fewer than two options")]
    [Arguments("""{"questions":[{"question":"Q?","options":[{"label":"A"},{"label":""}]}]}""", "a blank option label")]
    public void TryParse_WithAnInvalidCall_FailsWithAReasonInsteadOfPrompting(string json, string why)
    {
        // The runner is the FIRST guard on these arguments: ask_user is approval-required, so it is intercepted before
        // ToolArgumentRepairAIFunction (which normally coerces and repairs) ever runs. Nothing unvalidated may reach a
        // human, so a bad call must fail here with a reason the model can act on.
        var parsed = UserQuestionParser.TryParse(Arguments(json), out var questions, out var error);

        AssertEx.False(parsed, $"{why} must be rejected");
        AssertEx.Null(questions);
        AssertEx.NotNullOrEmpty(error);
    }

    [Test]
    public void TryParse_WithTooManyQuestionsOrOptions_IsRejected()
    {
        var question = """{"question":"Q?","options":[{"label":"A"},{"label":"B"}]}""";
        AssertEx.False(UserQuestionParser.TryParse(Arguments($$"""{"questions":[{{question}},{{question}},{{question}},{{question}},{{question}}]}"""), out _, out _),
            "five questions exceeds the schema's maxItems of 4");

        var sevenOptions = string.Join(',', Enumerable.Range(0, 7).Select(index => $$"""{"label":"opt-{{index}}"}"""));
        AssertEx.False(UserQuestionParser.TryParse(Arguments($$"""{"questions":[{"question":"Q?","options":[{{sevenOptions}}]}]}"""), out _, out _),
            "seven options exceeds the schema's maxItems of 6");
    }

    [Test]
    public void TryParse_WithNoArguments_IsRejected()
    {
        AssertEx.False(UserQuestionParser.TryParse(arguments: null, out _, out var nullError));
        AssertEx.NotNullOrEmpty(nullError);
        AssertEx.False(UserQuestionParser.TryParse(new Dictionary<string, object?>(StringComparer.Ordinal), out _, out var emptyError));
        AssertEx.NotNullOrEmpty(emptyError);
    }

    [Test]
    public void Answered_EmitsTheAnsweredEnvelopeWithAStableAnswerShape()
    {
        var json = UserQuestionResults.Answered([
            new UserQuestionAnswer("Which auth method?", ["OAuth device flow", "API key"], Other: null),
            new UserQuestionAnswer("Anything else?", [], "use mTLS")
        ]);

        var root = JsonDocument.Parse(json).RootElement;
        AssertEx.True(root.GetProperty("answered").GetBoolean());
        var answers = root.GetProperty("answers");
        AssertEx.Equal(expected: 2, answers.GetArrayLength());
        AssertEx.Equal("Which auth method?", answers[0].GetProperty("question").GetString());
        AssertEx.Equal(expected: 2, answers[0].GetProperty("selected").GetArrayLength());
        AssertEx.Equal(JsonValueKind.Null,
            answers[0].GetProperty("other").ValueKind,
            "\"other\" must be emitted even when null so the answer shape never changes between calls");
        AssertEx.Equal("use mTLS", answers[1].GetProperty("other").GetString());
    }

    [Test]
    [Arguments(UserQuestionResults.TimeoutReason)]
    [Arguments(UserQuestionResults.NotCollectedReason)]
    [Arguments(UserQuestionResults.MalformedCallReason)]
    public void Unanswered_IsDistinguishableFromAnEmptySelection(string reason)
    {
        // "the user chose nothing" and "the user was never asked" must never be confusable: the first is
        // answered:true with an empty selected[], the second is always answered:false with a named reason.
        var root = JsonDocument.Parse(UserQuestionResults.Unanswered(reason, "detail.")).RootElement;

        AssertEx.False(root.GetProperty("answered").GetBoolean());
        AssertEx.Equal(reason, root.GetProperty("reason").GetString());
        AssertEx.NotNullOrEmpty(root.GetProperty("message").GetString());
        AssertEx.False(root.TryGetProperty("answers", out _), "a not-answered result must carry no answers array");
    }

    [Test]
    public void Unanswered_ForAMalformedCall_TellsTheModelWhatToFix()
    {
        var message = JsonDocument.Parse(UserQuestionResults.Unanswered(UserQuestionResults.MalformedCallReason, "\"questions\" was missing."))
                                  .RootElement.GetProperty("message")
                                  .GetString();

        AssertEx.Contains(message, "\"questions\" was missing.");
        AssertEx.Contains(message, "ask_user again");
    }

    // Mirrors what FunctionCallContent.Arguments carries: a name→value bag whose values are JsonElement in production.
    private static Dictionary<string, object?> Arguments(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, SerializerOptions)!;
    }
}
