namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Velopack;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the version comparison the whole feature rests on, against the ordering verified by executing Velopack's
///     own <c>SemanticVersion</c> (<c>research/velopack-1.2.0-verification.md</c>).
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AppUpdateVersionsTests
{
    /// <summary>The verified ordering, ascending. Adjacent pairs are asserted in both directions.</summary>
    private static readonly string[] AscendingOrder =
    [
        "1.0.0-rc.2",
        "1.0.0-rc.2.dev.20260922.1",
        "1.0.0-rc.2.dev.20260923.1",
        "1.0.0-rc.3",
        "1.0.0",
        "1.0.1-dev.20260922.1",
        "1.0.1-rc.1",
        "1.0.1"
    ];

    [Test]
    public void IsHigher_AcrossTheVerifiedOrdering_AgreesWithVelopack()
    {
        for (var index = 0; index < AscendingOrder.Length - 1; index++)
        {
            var lower = AscendingOrder[index];
            var higher = AscendingOrder[index + 1];

            AssertEx.True(AppUpdateVersions.IsHigher(higher, lower), $"'{higher}' should outrank '{lower}'.");
            AssertEx.False(AppUpdateVersions.IsHigher(lower, higher), $"'{lower}' should not outrank '{higher}'.");
        }
    }

    [Test]
    [Arguments("1.0.0")]
    [Arguments("1.0.0-rc.2.dev.20260922.1")]
    public void IsHigher_ForEqualVersions_IsFalse(string version)
    {
        // "Strictly" higher: a tie across two feeds must produce one offer, not two, and never an offer of the
        // version already installed.
        AssertEx.False(AppUpdateVersions.IsHigher(version, version));
    }

    [Test]
    public void IsHigher_WithNoIncumbent_AcceptsAnyParseableCandidate()
    {
        AssertEx.True(AppUpdateVersions.IsHigher("0.0.1", incumbent: null));
        AssertEx.True(AppUpdateVersions.IsHigher("0.0.1", "not-a-version"));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("latest")]
    public void IsHigher_WithAnUnparseableCandidate_IsFalse(string? candidate)
    {
        AssertEx.False(AppUpdateVersions.IsHigher(candidate, "1.0.0"));
        AssertEx.False(AppUpdateVersions.IsHigher(candidate, incumbent: null));
    }

    [Test]
    public void NewestStable_IgnoresPrereleasesAndDeltas()
    {
        var newest = AppUpdateVersions.NewestStable(
        [
            Asset("1.0.0", VelopackAssetType.Full),
            Asset("1.0.1-rc.1", VelopackAssetType.Full),
            Asset("1.1.0", VelopackAssetType.Delta)
        ]);

        AssertEx.Equal("1.0.0", newest);
    }

    [Test]
    public void NewestStable_WhenTheFeedHoldsNoStableRelease_IsNull()
    {
        AssertEx.Null(AppUpdateVersions.NewestStable([Asset("1.0.0-rc.1", VelopackAssetType.Full)]));
        AssertEx.Null(AppUpdateVersions.NewestStable([]));
    }

    [Test]
    [Arguments("1.0.0", false)]
    [Arguments("1.0.0-rc.1", true)]
    [Arguments("1.0.0-rc.2.dev.20260922.1", true)]
    public void IsPrerelease_SeparatesTheThreeShapes(string version, bool expected)
    {
        AssertEx.Equal(expected, AppUpdateVersions.IsPrerelease(version));
    }

    private static VelopackAsset Asset(string version, VelopackAssetType type)
    {
        return new VelopackAsset
        {
            PackageId = "XE-Local-AI-Engine",
            Version = SemanticVersion.Parse(version),
            Type = type,
            FileName = $"XE-Local-AI-Engine-{version}-full.nupkg",
            SHA1 = "a",
            SHA256 = "b",
            Size = 1
        };
    }
}
