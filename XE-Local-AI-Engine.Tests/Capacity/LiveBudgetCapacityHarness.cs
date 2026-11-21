namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     A <see cref="CapacityService" /> whose footprints come from the real <see cref="ModelFootprintProvider" /> and
///     <see cref="ProcessContextAllocationResolver" />, over a WDDM-shaped GPU profile: a stable per-process budget and a lower global free reading.
/// </summary>
internal sealed class LiveBudgetCapacityHarness
{
    public const long Mib = 1024L * 1024;
    public const long Gb = 1024L * Mib;

    // Measured on a WSL2 (WDDM) host on 2026-11-20: the --list-devices process budget.
    public const long WddmProcessBudget = 30927 * Mib;

    public LiveBudgetCapacityHarness(IReadOnlyDictionary<string, GgufModelFootprintFacts> models,
        long globalFree,
        bool liveShareHybrid = true,
        long availableRam = 60 * Gb,
        LlamaServerSupervisorOptions? supervisorOptions = null,
        ResolvedLaunchArguments? resolved = null)
    {
        Profile = new HardwareProfile
        {
            TotalRamBytes = Math.Max(64 * Gb, availableRam),
            AvailableRamBytes = availableRam,
            VramBytes = 32 * Gb,
            AvailableVramBytes = globalFree,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };

        Audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Profile));
        Audit.GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new RuntimeDeviceAuditState
             {
                 InferenceBackend = "cuda",
                 GpuExpected = true,
                 CpuFallback = false
             }));

        var store = Substitute.For<IGgufModelStore>();
        store.ResolveModelFootprintFactsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult<GgufModelFootprintFacts?>(models.GetValueOrDefault(call.ArgAt<string>(0))));
        store.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("/models/model.gguf"));

        var budgetProbe = Substitute.For<IProcessVramBudgetProbe>();
        budgetProbe.GetProcessBudgetBytesOrNullAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(WddmProcessBudget));

        var variantSelector = Substitute.For<IGpuVariantSelector>();
        variantSelector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GpuVariant.Cuda));
        var profileResolver = Substitute.For<IInferenceProfileResolver>();
        profileResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult(resolved ?? ResolvedLaunchArguments.Explore()));

        Resolver = new ProcessContextAllocationResolver(store,
            Audit,
            budgetProbe,
            new MemoryFitEstimator(),
            new LlamaServerLaunchPolicyOptions(),
            liveShareHybrid);
        var footprints = new ModelFootprintProvider(variantSelector, profileResolver, Resolver, store);

        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        var providers = Substitute.For<ILocalModelProviderResolver>();
        providers.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LlamaServerProcessHealth>>([]));

        Service = new CapacityService(cloud,
            providers,
            Audit,
            supervisor,
            footprints,
            Ledger,
            new ProcessLaunchAdmissionRegistry(),
            new LlamaServerExternalEndpointOptions(),
            supervisorOptions ?? new LlamaServerSupervisorOptions(),
            NullLogger<CapacityService>.Instance);
    }

    public HardwareProfile Profile { get; }

    public IRuntimeDeviceAudit Audit { get; } = Substitute.For<IRuntimeDeviceAudit>();

    public ProcessContextAllocationResolver Resolver { get; }

    public CapacityService Service { get; }

    public PendingFootprintLedger Ledger { get; } = new();

    public static long UsableGpuBudget(long budget) =>
        budget - Math.Max(LlamaServerLaunchPolicyOptions.MinimumGpuReserveBytes, (long)(budget * LlamaServerLaunchPolicyOptions.DefaultGpuReserveFraction));
}
