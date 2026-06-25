namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

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
        IGpuVariantSelector? variantSelector = null)
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
            externalEndpoints,
            timeProvider ?? new AdvanceableTimeProvider());
    }
}
