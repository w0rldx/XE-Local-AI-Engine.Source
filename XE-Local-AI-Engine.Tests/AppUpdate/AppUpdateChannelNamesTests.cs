namespace XE_Local_AI_Engine.Tests.AppUpdate;

using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Pins the single channel vocabulary shared by the wire, node settings and the flavour configuration files.</summary>
[Category(TestCategories.Unit)]
public sealed class AppUpdateChannelNamesTests
{
    [Test]
    [Arguments(AppUpdateChannelNames.Stable, AppUpdateChannel.Stable)]
    [Arguments(AppUpdateChannelNames.Preview, AppUpdateChannel.Preview)]
    [Arguments(AppUpdateChannelNames.Development, AppUpdateChannel.Development)]
    public void TryParse_ForEachWireLiteral_ReturnsTheMatchingChannel(string literal, AppUpdateChannel expected)
    {
        AssertEx.True(AppUpdateChannelNames.TryParse(literal, out var channel));
        AssertEx.Equal(expected, channel);
    }

    [Test]
    [Arguments("Stable")]
    [Arguments(" stable ")]
    [Arguments("nightly")]
    [Arguments("")]
    [Arguments(null)]
    public void TryParse_ForAnUnknownOrDifferentlyCasedValue_Fails(string? value)
    {
        AssertEx.False(AppUpdateChannelNames.TryParse(value, out _));
    }

    [Test]
    public void All_ListsTheThreeChannelsInPresentationOrder()
    {
        AssertEx.Equal("stable,preview,development", string.Join(',', AppUpdateChannelNames.All));
    }

    [Test]
    public void ToWire_RoundTripsEveryEnumValue()
    {
        foreach (var channel in Enum.GetValues<AppUpdateChannel>())
        {
            var wire = AppUpdateChannelNames.ToWire(channel);
            AssertEx.True(AppUpdateChannelNames.TryParse(wire, out var parsed), $"'{wire}' is not a known literal.");
            AssertEx.Equal(channel, parsed);
            AssertEx.Contains(AppUpdateChannelNames.All, wire);
        }
    }
}
