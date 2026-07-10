namespace XE_Local_AI_Engine.Tests.ModelFit.Catalog;

using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelCatalogArchGate" /> numeric <c>bNNNN</c> tag comparison: an entry whose <c>minLlamaCppTag</c> the
///     node's runtime satisfies is supported; one whose architecture support landed in a newer build is excluded.
///     Malformed tags fail OPEN (never silently hide every entry).
/// </summary>
public sealed class ModelCatalogArchGateTests
{
    [Test]
    public void ParseBNumber_WhenWellFormed_ReturnsNumber()
    {
        AssertEx.Equal(expected: 9692, ModelCatalogArchGate.ParseBNumber("b9692")!.Value);
        AssertEx.Equal(expected: 100, ModelCatalogArchGate.ParseBNumber("B100")!.Value);
    }

    [Test]
    public void ParseBNumber_WhenMalformed_ReturnsNull()
    {
        AssertEx.Null(ModelCatalogArchGate.ParseBNumber(null));
        AssertEx.Null(ModelCatalogArchGate.ParseBNumber(""));
        AssertEx.Null(ModelCatalogArchGate.ParseBNumber("9692"));
        AssertEx.Null(ModelCatalogArchGate.ParseBNumber("b"));
        AssertEx.Null(ModelCatalogArchGate.ParseBNumber("bNaN"));
        AssertEx.Null(ModelCatalogArchGate.ParseBNumber("b-5"));
    }

    [Test]
    public void Supports_WhenInstalledAtOrAboveMinimum_ReturnsTrue()
    {
        AssertEx.True(ModelCatalogArchGate.Supports("b9700", "b9692"));
        AssertEx.True(ModelCatalogArchGate.Supports("b9692", "b9692"));
    }

    [Test]
    public void Supports_WhenInstalledBelowMinimum_ReturnsFalse()
    {
        AssertEx.False(ModelCatalogArchGate.Supports("b9000", "b9692"));
    }

    [Test]
    public void Supports_WhenMinimumTagUnparseable_ReturnsTrue()
    {
        // No meaningful floor declared → never gates the entry out.
        AssertEx.True(ModelCatalogArchGate.Supports("b9000", "not-a-tag"));
    }

    [Test]
    public void Supports_WhenInstalledTagUnparseable_FailsOpen()
    {
        // An unparseable installed/pinned tag must never silently hide every catalog entry.
        AssertEx.True(ModelCatalogArchGate.Supports("unknown", "b9692"));
        AssertEx.True(ModelCatalogArchGate.Supports(null, "b9692"));
    }
}
