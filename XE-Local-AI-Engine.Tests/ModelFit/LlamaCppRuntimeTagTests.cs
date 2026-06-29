namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.LlamaCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for <see cref="LlamaCppRuntimeTag.IsUpdateAvailable" />. An update is offered only when the installed
///     <c>b&lt;number&gt;</c> tag is strictly OLDER than the recommended one — a string inequality (the prior bug) wrongly
///     advertised a downgrade as an update when the installed tag was newer than the recommended one.
/// </summary>
public sealed class LlamaCppRuntimeTagTests
{
    [Test]
    public void IsUpdateAvailable_WhenInstalledIsOlderThanRecommended_ReturnsTrue()
    {
        AssertEx.True(LlamaCppRuntimeTag.IsUpdateAvailable("b9692", "b9700"),
            "An older installed build than the recommended one is an available update.");
    }

    [Test]
    public void IsUpdateAvailable_WhenInstalledEqualsRecommended_ReturnsFalse()
    {
        AssertEx.False(LlamaCppRuntimeTag.IsUpdateAvailable("b9700", "b9700"),
            "An installed build equal to the recommended one is not an update.");
    }

    [Test]
    public void IsUpdateAvailable_WhenInstalledIsNewerThanRecommended_ReturnsFalse()
    {
        // The original bug: a string inequality advertised this downgrade (b9700 -> b9692) as an update.
        AssertEx.False(LlamaCppRuntimeTag.IsUpdateAvailable("b9700", "b9692"),
            "A newer installed build than the recommended one must never advertise a downgrade as an update.");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void IsUpdateAvailable_WhenInstalledIsMissing_ReturnsTrueForFreshNode(string? installedTag)
    {
        AssertEx.True(LlamaCppRuntimeTag.IsUpdateAvailable(installedTag, "b9700"),
            "A fresh node (no installed runtime) must be offered the recommended install.");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void IsUpdateAvailable_WhenRecommendedIsMissing_ReturnsFalse(string? recommendedTag)
    {
        AssertEx.False(LlamaCppRuntimeTag.IsUpdateAvailable("b9700", recommendedTag),
            "Nothing recommended means there is no update to offer.");
    }

    [Test]
    public void IsUpdateAvailable_WhenInstalledTagIsMalformed_FallsBackToStringInequality()
    {
        // Unexpected non-b<number> installed tag: fall back to the prior differs-means-update behavior without throwing.
        AssertEx.True(LlamaCppRuntimeTag.IsUpdateAvailable("v1.2.3", "b9700"),
            "A malformed installed tag that differs from the recommended one falls back to update-available.");
    }

    [Test]
    public void IsUpdateAvailable_WhenRecommendedTagIsMalformed_FallsBackToStringInequality()
    {
        AssertEx.True(LlamaCppRuntimeTag.IsUpdateAvailable("b9700", "latest"),
            "A malformed recommended tag that differs from the installed one falls back to update-available.");
    }

    [Test]
    public void IsUpdateAvailable_WhenBothTagsAreEqualAndMalformed_FallsBackToFalse()
    {
        AssertEx.False(LlamaCppRuntimeTag.IsUpdateAvailable("latest", "latest"),
            "Identical malformed tags fall back to not-an-update (no string difference).");
    }

    [Test]
    [Arguments("b9700", 9700L)]
    [Arguments("B9700", 9700L)]
    [Arguments("b0", 0L)]
    public void TryParseTagNumber_WhenWellFormed_ReturnsNumber(string tag, long expected)
    {
        AssertEx.Equal(expected, LlamaCppRuntimeTag.TryParseTagNumber(tag));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("9700")]
    [Arguments("b")]
    [Arguments("b97a")]
    [Arguments("v1.2.3")]
    public void TryParseTagNumber_WhenMalformed_ReturnsNull(string? tag)
    {
        AssertEx.Null(LlamaCppRuntimeTag.TryParseTagNumber(tag));
    }
}
