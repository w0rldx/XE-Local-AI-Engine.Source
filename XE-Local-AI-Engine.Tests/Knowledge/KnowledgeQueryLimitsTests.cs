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
}
