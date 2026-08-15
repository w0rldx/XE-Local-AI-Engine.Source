namespace XE_Local_AI_Engine.Tests.Training;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ToolMockingTests
{
    private const string Schema = """{"type":"object","properties":{"path":{"type":"string"},"depth":{"type":"integer"}}}""";

    private static readonly ToolMockStaticVerifier Verifier = new();
    private static readonly ToolMockEngine Engine = new();

    [Test]
    [Arguments("{{ path }}")]
    [Arguments("${HOME}/secrets")]
    [Arguments("$(cat /etc/passwd)")]
    [Arguments("`whoami`")]
    [Arguments("=1+1")]
    [Arguments("<% eval %>")]
    [Arguments("#{expr}")]
    public async Task MockVerifier_ExpressionLikeRule_Fails(string value)
    {
        var body = Body(new ToolMockRuleV1("path", ToolMockMatchKind.Equality, value, null, "ok"));

        var verification = Verifier.Verify(body, Schema);

        AssertEx.False(verification.Passed, "An expression-like match value must never be admitted.");
        AssertEx.Contains(verification.Findings, finding => finding.Contains("expression-like", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    [Test]
    public void MockVerifier_FieldOutsideTheParameterSchema_Fails()
    {
        var body = Body(new ToolMockRuleV1("not_a_parameter", ToolMockMatchKind.Presence, null, null, "ok"));

        var verification = Verifier.Verify(body, Schema);

        AssertEx.False(verification.Passed);
        AssertEx.Contains(verification.Findings, finding => finding.Contains("not_a_parameter", StringComparison.Ordinal));
    }

    [Test]
    public void MockVerifier_RuleCountBound_IsEnforced()
    {
        var rules = Enumerable.Range(0, ToolMockBodyV1.MaxRules + 1)
                              .Select(_ => new ToolMockRuleV1("path", ToolMockMatchKind.Presence, null, null, "ok"))
                              .ToArray();

        var verification = Verifier.Verify(new ToolMockBodyV1
        {
            Rules = rules
        }, Schema);

        AssertEx.False(verification.Passed);
        AssertEx.Contains(verification.Findings, finding => finding.Contains("at most", StringComparison.Ordinal));
    }

    [Test]
    public void MockVerifier_OversizedResponse_Fails()
    {
        var body = Body(new ToolMockRuleV1("path", ToolMockMatchKind.Presence, null, null, new string('x', ToolMockBodyV1.MaxResponseLength + 1)));

        var verification = Verifier.Verify(body, Schema);

        AssertEx.False(verification.Passed);
    }

    [Test]
    public void MockVerifier_LiteralRule_Passes()
    {
        var body = Body(new ToolMockRuleV1("path", ToolMockMatchKind.Equality, "README.md", null, "# Title"));

        var verification = Verifier.Verify(body, Schema);

        AssertEx.True(verification.Passed, string.Join("; ", verification.Findings));
    }

    [Test]
    public void MockVerifier_MalformedBody_ReturnsAFailingVerdictInsteadOfThrowing()
    {
        var parsed = Verifier.TryParse("not json"u8, out var body, out var failureReason);

        AssertEx.False(parsed);
        AssertEx.Null(body);
        AssertEx.NotNullOrEmpty(failureReason);
    }

    [Test]
    public void MockEngine_NoRuleMatches_AndNoDefault_ReturnsNull()
    {
        var body = Body(new ToolMockRuleV1("path", ToolMockMatchKind.Equality, "OTHER.md", null, "# Other"));

        var response = Engine.TryRespond(body, Arguments("""{"path":"README.md"}"""));

        AssertEx.Null(response, "An unmatched call must not fall through to anything — the caller reports validation-only.");
    }

    [Test]
    public void MockEngine_FirstMatchingRuleWins()
    {
        var body = new ToolMockBodyV1
        {
            Rules =
            [
                new ToolMockRuleV1("path", ToolMockMatchKind.Equality, "README.md", null, "first"),
                new ToolMockRuleV1("path", ToolMockMatchKind.Presence, null, null, "second")
            ],
            DefaultResponse = "default"
        };

        AssertEx.Equal("first", Engine.TryRespond(body, Arguments("""{"path":"README.md"}""")));
        AssertEx.Equal("second", Engine.TryRespond(body, Arguments("""{"path":"other.md"}""")));
        AssertEx.Equal("default", Engine.TryRespond(body, Arguments("""{"depth":2}""")));
    }

    [Test]
    public void MockEngine_EnumMatch_ComparesAgainstEveryCandidate()
    {
        var body = Body(new ToolMockRuleV1("path", ToolMockMatchKind.Enum, null, ["a.md", "b.md"], "matched"));

        AssertEx.Equal("matched", Engine.TryRespond(body, Arguments("""{"path":"b.md"}""")));
        AssertEx.Null(Engine.TryRespond(body, Arguments("""{"path":"c.md"}""")));
    }

    private static ToolMockBodyV1 Body(ToolMockRuleV1 rule) =>
        new()
        {
            Rules = [rule]
        };

    private static JsonElement Arguments(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
