namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The restart-gated tunables reach the provider option objects through the composition-root seeds, and at their
///     defaults those objects equal what the provider built before the settings existed.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class NodeSettingsProviderSeedingTests
{
    [Test]
    public void SupervisorSeed_WithDefaults_EqualsTheProviderDefaults()
    {
        using var services = ServicesWith(StubNodeRuntimeSettings.Create().Build());

        var seeded = AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions(services);
        var providerDefault = new LlamaServerSupervisorOptions();

        AssertEx.Equal(providerDefault.ReadinessTimeoutCap, seeded.ReadinessTimeoutCap);
        AssertEx.Equal(providerDefault.HttpNetworkTimeout, seeded.HttpNetworkTimeout);
        AssertEx.Equal(providerDefault.EmbeddingHttpNetworkTimeout, seeded.EmbeddingHttpNetworkTimeout);
        AssertEx.Equal(providerDefault.ChatCacheRamMiB, seeded.ChatCacheRamMiB, "an absent cache budget must stay the RAM-derived automatic value.");
    }

    [Test]
    public void SupervisorSeed_CarriesStoredOverrides()
    {
        var runtimeSettings = StubNodeRuntimeSettings.Create().Build();
        runtimeSettings.GetLlamaReadinessTimeoutCap().Returns(TimeSpan.FromSeconds(900));
        runtimeSettings.GetLlamaChatHttpTimeout().Returns(TimeSpan.FromSeconds(7200));
        runtimeSettings.GetLlamaEmbeddingHttpTimeout().Returns(TimeSpan.FromSeconds(60));
        runtimeSettings.GetLlamaChatCacheRamMiB().Returns(0);
        using var services = ServicesWith(runtimeSettings);

        var seeded = AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions(services);

        AssertEx.Equal(TimeSpan.FromSeconds(900), seeded.ReadinessTimeoutCap);
        AssertEx.Equal(TimeSpan.FromSeconds(7200), seeded.HttpNetworkTimeout);
        AssertEx.Equal(TimeSpan.FromSeconds(60), seeded.EmbeddingHttpNetworkTimeout);
        AssertEx.Equal(expected: 0, seeded.ChatCacheRamMiB);
        seeded.Validate();
    }

    [Test]
    public void SupervisorSeed_AtTheLowestStorableReadinessCap_StillValidates()
    {
        // The stored minimum is the supervisor's readiness base; one below it would take the node down at host build.
        var runtimeSettings = StubNodeRuntimeSettings.Create().Build();
        runtimeSettings.GetLlamaReadinessTimeoutCap().Returns(TimeSpan.FromSeconds(StoredNodeSettings.MinLlamaReadinessTimeoutCapSeconds));
        using var services = ServicesWith(runtimeSettings);

        var seeded = AddNodeModelRuntimeExtensions.BuildSeededLlamaServerSupervisorOptions(services);

        seeded.Validate();
        AssertEx.Equal(seeded.ReadinessBaseTimeout, seeded.ReadinessTimeoutCap);
    }

    [Test]
    public void HuggingFaceSeed_CarriesTheDownloadConnections()
    {
        var runtimeSettings = StubNodeRuntimeSettings.Create().Build();
        runtimeSettings.GetHuggingFaceDownloadConnections().Returns(7);
        using var services = ServicesWith(runtimeSettings);

        var seeded = AddNodeModelRuntimeExtensions.BuildSeededHuggingFaceOptions(services, new ConfigurationBuilder().Build());

        AssertEx.Equal(expected: 7, seeded.DownloadConnections);
        AssertEx.Equal(new HuggingFaceOptions().DownloadConnections, StoredNodeSettings.DefaultHuggingFaceDownloadConnections);
    }

    private static ServiceProvider ServicesWith(INodeRuntimeSettings runtimeSettings) =>
        new ServiceCollection().AddSingleton(runtimeSettings).BuildServiceProvider();
}
