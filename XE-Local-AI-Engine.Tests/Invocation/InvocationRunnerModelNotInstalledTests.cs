namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static InvocationRunnerTestSupport;

/// <summary>
///     The whole refusal of a chat turn on a missing model, over the real allocation resolver, footprint provider, capacity
///     gate, launch admission and invocation runner. Only the empty model store and the machine probes are substituted.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InvocationRunnerModelNotInstalledTests
{
    private const string AbsentModel = "absent/model-GGUF:Q4_K_M";

    [Test]
    public async Task ChatTurn_OnAModelThatIsNotInstalled_FailsAsModelNotInstalled()
    {
        var profile = new HardwareProfile
        {
            TotalRamBytes = 64L * 1024 * 1024 * 1024,
            AvailableRamBytes = 48L * 1024 * 1024 * 1024,
            VramBytes = 32L * 1024 * 1024 * 1024,
            AvailableVramBytes = 30L * 1024 * 1024 * 1024,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500L * 1024 * 1024 * 1024
        };
        var audit = Substitute.For<IRuntimeDeviceAudit>();
        audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(profile));
        audit.GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new RuntimeDeviceAuditState
        {
            InferenceBackend = "cuda",
            GpuExpected = true,
            CpuFallback = false
        }));

        await using var services = BuildServices(audit);
        var admission = new PooledRoleLaunchAdmission(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<PooledRoleLaunchAdmission>.Instance);
        var evictions = 0;

        // The agent factory is where a real turn first ensures its model's process, which runs this admission.
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>()).Returns(_ => EnsureModelAsync());
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        await RunAsync(runner, RuntimePackageBuilder.Valid().Build());

        await dispatcher.Received(1).ReportInvocationFailedAsync(Arg.Any<Guid>(), InvocationFailureMessages.ModelUnavailable, FailureCategory.ModelNotInstalled);
        AssertEx.Equal(0, evictions);

        async Task<InvocationAgentContext> EnsureModelAsync()
        {
            await admission.AdmitChatAsync(AbsentModel,
                mayUnloadIdleModels: true,
                (_, _) =>
                {
                    evictions++;
                    return Task.FromResult(new IdleChatEvictionResult(null, null));
                },
                CancellationToken.None);
            throw new InvalidOperationException("The admission must have refused the launch.");
        }
    }

    private static ServiceProvider BuildServices(IRuntimeDeviceAudit audit)
    {
        // A substituted store answers null for every lookup: nothing is installed.
        var store = Substitute.For<IGgufModelStore>();
        var allocationResolver = new ProcessContextAllocationResolver(store,
            audit,
            Substitute.For<IProcessVramBudgetProbe>(),
            new MemoryFitEstimator(),
            new LlamaServerLaunchPolicyOptions());
        var profileResolver = Substitute.For<IInferenceProfileResolver>();
        profileResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult(ResolvedLaunchArguments.Explore()));
        var variantSelector = Substitute.For<IGpuVariantSelector>();
        variantSelector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GpuVariant.Cuda));
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult("llamacpp"));
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LlamaServerProcessHealth>>([]));

        var services = new ServiceCollection();
        services.AddSingleton(audit);
        services.AddScoped<ICapacityService>(_ => new CapacityService(Substitute.For<IActiveCloudChatClientFactory>(),
            providerResolver,
            audit,
            supervisor,
            new ModelFootprintProvider(variantSelector, profileResolver, allocationResolver, store),
            new PendingFootprintLedger(),
            new ProcessLaunchAdmissionRegistry(),
            new LlamaServerExternalEndpointOptions(),
            new LlamaServerSupervisorOptions(),
            NullLogger<CapacityService>.Instance));
        return services.BuildServiceProvider();
    }
}
