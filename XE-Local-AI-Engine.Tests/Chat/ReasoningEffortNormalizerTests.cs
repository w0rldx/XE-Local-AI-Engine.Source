namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ReasoningEffortNormalizerTests
{
    [Test]
    [Arguments("none")]
    [Arguments("on")]
    [Arguments("minimal")]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    [Arguments("xhigh")]
    public void Normalize_WhenCanonicalEffort_ReturnsSameValue(string effort)
    {
        AssertEx.Equal(effort, ReasoningEffortNormalizer.Normalize(effort));
    }

    [Test]
    [Arguments("On", "on")]
    [Arguments("ON", "on")]
    [Arguments("  High  ", "high")]
    [Arguments("NONE", "none")]
    [Arguments("Minimal", "minimal")]
    [Arguments("XHIGH", "xhigh")]
    public void Normalize_WhenMixedCaseOrPadded_ReturnsCanonicalLowercase(string input, string expected)
    {
        AssertEx.Equal(expected, ReasoningEffortNormalizer.Normalize(input));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("reason")]
    [Arguments("off")]
    [Arguments("true")]
    public void Normalize_WhenBlankOrUnknown_ReturnsNull(string? input)
    {
        AssertEx.Null(ReasoningEffortNormalizer.Normalize(input));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("none")]
    [Arguments("on")]
    [Arguments("minimal")]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    [Arguments("xhigh")]
    [Arguments("HIGH")]
    public void IsValid_WhenBlankOrRecognized_ReturnsTrue(string? input)
    {
        AssertEx.True(ReasoningEffortNormalizer.IsValid(input));
    }

    [Test]
    [Arguments("reason")]
    [Arguments("off")]
    [Arguments("true")]
    [Arguments("medium-high")]
    public void IsValid_WhenNonBlankUnrecognized_ReturnsFalse(string input)
    {
        AssertEx.False(ReasoningEffortNormalizer.IsValid(input));
    }
}
