namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The node registers exactly one GPU-work gate, so training, benchmarks and image jobs all admit through the same instance.</summary>
[Category(TestCategories.Integration)]
public sealed class GpuWorkGateCompositionTests
{
    [Test]
    public async Task Composition_RegistersOneSingletonGate_ThatEveryScopeShares()
    {
        // The three modules that once each registered or consumed the gate, in AddNodeApplication's order.
        var builder = Host.CreateApplicationBuilder();
        builder.AddNodeTrainingDatasets();
        builder.AddNodeCapacity(builder.Configuration);
        builder.AddNodeTrainingRuns();

        var registrations = builder.Services.Where(static d => d.ServiceType == typeof(IGpuWorkGate)).ToList();
        AssertEx.Equal(1, registrations.Count);
        AssertEx.Equal(ServiceLifetime.Singleton, registrations[0].Lifetime);
        AssertEx.Equal(typeof(GpuWorkGate), registrations[0].ImplementationType);

        await using var provider = builder.Services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        AssertEx.True(ReferenceEquals(first.ServiceProvider.GetRequiredService<IGpuWorkGate>(), second.ServiceProvider.GetRequiredService<IGpuWorkGate>()),
            "Every scope must resolve the one node-wide gate.");
    }
}
