namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Boundary tests for the single shared query-length bound. Both the agent tool handler and the HTTP search endpoint
///     validate through <see cref="KnowledgeQueryLimits" />, so exercising it directly covers the enforced limit at both
///     sites (the endpoint's <c>HandleAsync</c> calls <see cref="KnowledgeQueryLimits.ExceedsMaxLength" /> verbatim).
/// </summary>
public sealed class KnowledgeQueryLimitsTests
{
    [Test]
    public void MaxQueryLength_IsUnifiedAtTheStricterBound()
    {
        AssertEx.Equal(expected: 1000, KnowledgeQueryLimits.MaxQueryLength);
    }

    [Test]
    public void ExceedsMaxLength_AtExactBound_IsAllowed()
    {
        AssertEx.False(KnowledgeQueryLimits.ExceedsMaxLength(new string('x', KnowledgeQueryLimits.MaxQueryLength)));
    }

    [Test]
    public void ExceedsMaxLength_OneOverBound_IsRejected()
    {
        AssertEx.True(KnowledgeQueryLimits.ExceedsMaxLength(new string('x', KnowledgeQueryLimits.MaxQueryLength + 1)));
    }

    [Test]
    public void ExceedsMaxLength_TrimsSurroundingWhitespaceBeforeMeasuring()
    {
        // Surrounding whitespace does not count toward the bound: a max-length core padded with spaces is still allowed.
        var padded = $"  {new string('x', KnowledgeQueryLimits.MaxQueryLength)}  ";

        AssertEx.False(KnowledgeQueryLimits.ExceedsMaxLength(padded));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void ValidateAndNormalize_WhenEmptyOrWhitespace_ReportsEmpty(string? rawQuery)
    {
        var validation = KnowledgeQueryLimits.ValidateAndNormalize(rawQuery, out var normalized);

        AssertEx.Equal(KnowledgeQueryValidation.Empty, validation);
        AssertEx.Equal(string.Empty, normalized);
    }

    [Test]
    public void ValidateAndNormalize_WhenPadded_ReturnsTrimmedNormalizedQuery()
    {
        var validation = KnowledgeQueryLimits.ValidateAndNormalize("   hello world   ", out var normalized);

        AssertEx.Equal(KnowledgeQueryValidation.Valid, validation);
        AssertEx.Equal("hello world", normalized);
    }

    [Test]
    public void ValidateAndNormalize_AtExactContentBound_IsValid()
    {
        var validation = KnowledgeQueryLimits.ValidateAndNormalize(new string('x', KnowledgeQueryLimits.MaxQueryLength), out var normalized);

        AssertEx.Equal(KnowledgeQueryValidation.Valid, validation);
        AssertEx.Equal(KnowledgeQueryLimits.MaxQueryLength, normalized.Length);
    }

    [Test]
    public void ValidateAndNormalize_WhenTrimmedContentOverBound_ReportsTooLong()
    {
        var validation = KnowledgeQueryLimits.ValidateAndNormalize(new string('x', KnowledgeQueryLimits.MaxQueryLength + 1), out var normalized);

        AssertEx.Equal(KnowledgeQueryValidation.TooLong, validation);
        AssertEx.Equal(string.Empty, normalized);
    }

    [Test]
    public void ValidateAndNormalize_WhenRawPayloadOverRawCap_ReportsTooLongBeforeTrimming()
    {
        // A short trimmed core wrapped in a huge whitespace pad: the raw transport cap rejects it up front, so a padded
        // giant can never slip past the trimmed content bound.
        var padded = new string(' ', KnowledgeQueryLimits.MaxRawQueryLength) + "hi";

        var validation = KnowledgeQueryLimits.ValidateAndNormalize(padded, out var normalized);

        AssertEx.Equal(KnowledgeQueryValidation.TooLong, validation);
        AssertEx.Equal(string.Empty, normalized);
    }
}
