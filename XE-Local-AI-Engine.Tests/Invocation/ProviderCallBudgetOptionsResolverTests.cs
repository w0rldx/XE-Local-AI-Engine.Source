namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

[Category(TestCategories.Unit)]
public sealed class ProviderCallBudgetOptionsResolverTests
{
    private static readonly ProviderCallBudgetOptions NodeOptions = new()
    {
        MaxProviderCallsPerInvocation = 11,
        ReservedOutputTokenFloor = 900,
        OversizedToolResultExcerptChars = 321
    };

    [Test]
    public async Task ResolveAsync_ReadsLiveNodeSettingsAndKeepsTheNodeFloorWithoutAnOverride()
    {
        var settings = StubNodeRuntimeSettings.Create()
                                              .WithProviderBudgetMaxCumulativeInputTokens(5_000)
                                              .WithProviderBudgetRecentMessagesToKeep(3)
                                              .Build();

        var resolved = await ProviderCallBudgetOptionsResolver.ResolveAsync(NodeOptions, settings, 16_384, null, CancellationToken.None);

        AssertEx.Equal(11, resolved.MaxProviderCallsPerInvocation);
        AssertEx.Equal(5_000, resolved.MaxCumulativeInputTokens);
        AssertEx.Equal(16_384, resolved.DefaultContextTokens);
        AssertEx.Equal(900, resolved.ReservedOutputTokenFloor);
        AssertEx.Equal(3, resolved.RecentMessagesToKeep);
        AssertEx.Equal(321, resolved.OversizedToolResultExcerptChars);
    }

    [Test]
    public async Task ResolveAsync_UsesAValidReservationOverrideAndIgnoresANegativeOne()
    {
        var settings = StubNodeRuntimeSettings.Create().Build();

        var overridden = await ProviderCallBudgetOptionsResolver.ResolveAsync(NodeOptions, settings, 8192, 2_048, CancellationToken.None);
        var negative = await ProviderCallBudgetOptionsResolver.ResolveAsync(NodeOptions, settings, 8192, -1, CancellationToken.None);

        AssertEx.Equal(2_048, overridden.ReservedOutputTokenFloor);
        AssertEx.Equal(900, negative.ReservedOutputTokenFloor);
    }
}
