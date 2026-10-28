namespace XE_Local_AI_Engine.Tests.Eval;

using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared golden-turns parse/validation used by authoring and eval-time scoring. A JSON <c>null</c> element
///     must be a malformed-turn validation failure, not a NullReferenceException escaping the eval loop or a 500.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GoldenInputTurnsTests
{
    [Test]
    public void TryParse_WhenOnlyTurnIsNull_ReturnsFalseWithoutThrowing()
    {
        var ok = GoldenInputTurns.TryParse("[null]", out var messages, out var error);

        AssertEx.False(ok, "A `[null]` turn is malformed and must be rejected.");
        AssertEx.Empty(messages);
        AssertEx.NotNullOrEmpty(error);
    }

    [Test]
    public void TryParse_WhenLaterTurnIsNull_ReturnsFalseWithoutThrowing()
    {
        var ok = GoldenInputTurns.TryParse("""[{"role":"user","text":"hi"},null]""", out var messages, out var error);

        AssertEx.False(ok, "A null element after a valid turn is still malformed and must be rejected.");
        AssertEx.Empty(messages);
        AssertEx.NotNullOrEmpty(error);
    }

    [Test]
    public void TryParse_WhenAllTurnsValid_Succeeds()
    {
        var ok = GoldenInputTurns.TryParse("""[{"role":"user","text":"hi"},{"role":"assistant","text":"hello"}]""",
            out var messages, out var error);

        AssertEx.True(ok, "Two well-formed turns must parse.");
        AssertEx.Null(error);
        AssertEx.Equal(expected: 2, messages.Count);
    }
}
