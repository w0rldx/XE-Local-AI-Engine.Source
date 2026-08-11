namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>Builds a <see cref="LlamaServerProcessSupervisor" /> over fakes with sensible test defaults.</summary>
internal static class SupervisorFactory
{
    public static LlamaServerProcessSupervisor Create(FakeProcessLauncher? launcher = null,
        ILlamaServerHealthProbe? healthProbe = null,
        FakeModelStore? modelStore = null,
        LlamaServerSupervisorOptions? options = null,
        LlamaServerExternalEndpointOptions? externalEndpoints = null,
        AdvanceableTimeProvider? timeProvider = null,
        IGpuVariantSelector? variantSelector = null,
        IInferenceProfileResolver? profileResolver = null,
        ILlamaServerLaunchPolicy? launchPolicy = null,
        LlamaServerLaunchPolicyOptions? launchPolicyOptions = null,
        ILlamaServerLaunchFallbackStore? launchFallbackStore = null,
        IGpuModelLoadAdmission? loadAdmission = null,
        ILlamaCppSourceBuildActivity? sourceBuildActivity = null,
        ILlamaFitParamsRunner? fitParamsRunner = null,
        ILlamaLayerPlacementReport? layerPlacementReport = null,
        IProcessContextAllocationResolver? allocationResolver = null,
        IProcessLaunchAdmissionRegistry? launchAdmissions = null,
        ILlamaServerExtraLaunchArgumentsResolver? extraArgumentsResolver = null)
    {
        return new LlamaServerProcessSupervisor(new FakeBinaryManager(),
            variantSelector ?? new FakeVariantSelector(),
            modelStore ?? new FakeModelStore(),
            launcher ?? new FakeProcessLauncher(),
            healthProbe ?? new FakeHealthProbe(),
            options ?? new LlamaServerSupervisorOptions
            {
                // A long TTL keeps the background reaper out of the way; tests drive eviction explicitly.
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 3,
                MaxRestartAttempts = 3
            },
            profileResolver ?? new FakeInferenceProfileResolver(),
            launchPolicy ?? new LlamaServerLaunchPolicy(launchPolicyOptions ?? new LlamaServerLaunchPolicyOptions(),
                launchFallbackStore ?? new FakeLaunchFallbackStore()),
            externalEndpoints,
            timeProvider ?? new AdvanceableTimeProvider(),
            loadAdmission: loadAdmission,
            sourceBuildActivity: sourceBuildActivity,
            fitParamsRunner: fitParamsRunner,
            allocationResolver: allocationResolver,
            layerPlacementReport: layerPlacementReport,
            launchAdmissions: launchAdmissions,
            extraArgumentsResolver: extraArgumentsResolver);
    }
}
