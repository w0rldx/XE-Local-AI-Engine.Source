namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The supervisor launches an admitted chat within its booking: fit target in the argv, over-booking retries refused, over-booked children logged.</summary>
[Category(TestCategories.Unit)]
public sealed class SupervisorAdmittedBookingTests
{
    private const long Mib = 1024L * 1024;
    private const long Gb = 1024L * Mib;
    private const string Model = "llama3";

    // Verbatim from a real CUDA allocation failure.
    private const string CudaOutOfMemoryLine =
        "0.27.495.305 E ggml_backend_cuda_buffer_type_alloc_buffer: allocating 2343750.00 MiB on device 0: cudaMalloc failed: out of memory";

    [Test]
    public async Task AdmittedHybridLiveShare_EmitsFitTargetFromTheBooking()
    {
        var allocation = Allocation(ProcessPlacementMode.Hybrid, new ResourceFootprint(27356 * Mib, 37 * Gb), window: 65536, slots: 1) with
        {
            FitTargetBytes = 3571 * Mib
        };
        var launcher = new FakeProcessLauncher();
        var registry = Admit(GpuVariant.Cuda, allocation, out var consumer);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchAdmissions: registry);

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        var arguments = spec!.Arguments.ToList();
        var fit = arguments.IndexOf("--fit");
        AssertEx.Equal("on", arguments[fit + 1]);
        AssertEx.Equal("--fit-target", arguments[fit + 2]);
        AssertEx.Equal("3571", arguments[fit + 3]);
        consumer.Dispose();
    }

    [Test]
    public async Task OomRetry_ExceedingTheBooking_IsRefused()
    {
        var admitted = Allocation(ProcessPlacementMode.ExpertOffload, new ResourceFootprint(6 * Gb, 21 * Gb), window: 65536, slots: 2);
        var resolver = Substitute.For<IProcessContextAllocationResolver>();
        resolver.TryGetEffectiveCommittedAllocation(Arg.Any<ProcessContextAllocation>(), out Arg.Any<ProcessContextAllocation>())
                .Returns(call =>
                {
                    call[1] = admitted;
                    return true;
                });

        // A retry priced at the stable budget: one slot fewer, but resident, so it books more GPU than the admission did. Offered once, so a
        // missing guard launches it and then ends the attempt loop instead of retrying forever.
        var offered = 0;
        resolver.TryDownTierAfterOutOfMemory(Arg.Any<ProcessContextAllocation>(), out Arg.Any<ProcessContextAllocation>())
                .Returns(call =>
                {
                    call[1] = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(25 * Gb, RamBytes: 0), window: 65536, slots: 1);
                    return Interlocked.Increment(ref offered) == 1;
                });
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [CudaOutOfMemoryLine]
        };
        var registry = Admit(GpuVariant.Cuda, admitted, out var consumer);
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new FakeHealthProbe(ready: false),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            allocationResolver: resolver,
            launchAdmissions: registry);

        var refused = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None));

        // The optimized attempt and its one safe retry ran; the out-of-memory retry beyond the booking never launched.
        AssertEx.Contains(refused.Message, "exceed the memory its admission booked");
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        consumer.Dispose();
    }

    [Test]
    public async Task ServedVariant_VulkanSelectedCudaServed_WithinBooking_Launches()
    {
        var admitted = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(10 * Gb, RamBytes: 0), window: 32768, slots: 2);
        var served = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(9 * Gb, RamBytes: 0), window: 16384, slots: 2);
        var launcher = new FakeProcessLauncher();
        var registry = Admit(GpuVariant.Vulkan, admitted, out var consumer);
        var resolver = ResolverServing(served);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Vulkan),
            allocationResolver: resolver,
            launchAdmissions: registry,
            binaryManager: new FakeBinaryManager(GpuVariant.Cuda));

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        // The served build launches on its own allocation, re-priced for the variant actually launched, not on the admitted window.
        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Empty(launcher.Launches);
        var arguments = spec!.Arguments.ToList();
        AssertEx.Equal("16384", arguments[arguments.IndexOf("--kv-unified-per-slot") + 1]);
        await resolver.Received().ResolveAsync(Model, ModelRole.Chat, GpuVariant.Cuda, Arg.Any<ResolvedLaunchArguments>(), Arg.Any<CancellationToken>());
        consumer.Dispose();
    }

    [Test]
    public async Task ServedVariant_LaterResolveBeyondTheBooking_LaunchesTheCheckedAllocation()
    {
        var admitted = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(10 * Gb, RamBytes: 0), window: 32768, slots: 2);

        // The first resolve is a transient allocation that fits; it is not cached, so any later resolve recovers to one beyond the booking.
        var transient = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(9 * Gb, RamBytes: 0), window: 16384, slots: 2);
        var recovered = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(14 * Gb, RamBytes: 0), window: 65536, slots: 2);
        var resolver = Substitute.For<IProcessContextAllocationResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<ResolvedLaunchArguments>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ProcessContextAllocation?>(transient), Task.FromResult<ProcessContextAllocation?>(recovered));
        var launcher = new FakeProcessLauncher();
        var registry = Admit(GpuVariant.Vulkan, admitted, out var consumer);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Vulkan),
            allocationResolver: resolver,
            launchAdmissions: registry,
            binaryManager: new FakeBinaryManager(GpuVariant.Cuda));

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        // The allocation checked against the booking is the one launched: one resolve, its window in the argv.
        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        var arguments = spec!.Arguments.ToList();
        AssertEx.Equal("16384", arguments[arguments.IndexOf("--kv-unified-per-slot") + 1]);
        await resolver.Received(1).ResolveAsync(Model, ModelRole.Chat, GpuVariant.Cuda, Arg.Any<ResolvedLaunchArguments>(), Arg.Any<CancellationToken>());
        consumer.Dispose();
    }

    [Test]
    public async Task OverBooking_ServedVariant_MeasuresAgainstTheBooking()
    {
        var admitted = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(10 * Gb, RamBytes: 0), window: 32768, slots: 2);
        var served = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(9 * Gb, RamBytes: 0), window: 16384, slots: 2);
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        var registry = Admit(GpuVariant.Vulkan, admitted, out var consumer);

        // 28 GiB free before the load, 6 GiB after: a 22 GiB take against the 10 GiB booked for the selected variant.
        await using var supervisor = SupervisorFactory.Create(new FakeProcessLauncher(),
            variantSelector: new FakeVariantSelector(GpuVariant.Vulkan),
            allocationResolver: ResolverServing(served),
            launchAdmissions: registry,
            binaryManager: new FakeBinaryManager(GpuVariant.Cuda),
            logger: logger,
            liveMemorySampler: new SequenceSampler(28 * Gb, 6 * Gb));

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        var warning = SingleOverBookingWarning(logger);
        AssertEx.Contains(warning, "booked 10240 MiB");
        AssertEx.Contains(warning, "observed 22528 MiB");
        consumer.Dispose();
    }

    [Test]
    public async Task ServedVariant_CpuSelectedCudaServed_Refused()
    {
        // A CPU admission books no GPU and a small window; the served CUDA build would load outside VRAM accounting.
        var admitted = Allocation(ProcessPlacementMode.Cpu, new ResourceFootprint(GpuBytes: 0, 6 * Gb), window: 4096, slots: 2);
        var served = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(9 * Gb, RamBytes: 0), window: 32768, slots: 2);

        await AssertServedVariantRefusedAsync(GpuVariant.Cpu, GpuVariant.Cuda, admitted, served);
    }

    [Test]
    public async Task ServedVariant_CudaSelectedCpuServed_Refused()
    {
        var admitted = Allocation(ProcessPlacementMode.GpuResident, new ResourceFootprint(9 * Gb, RamBytes: 0), window: 16384, slots: 2);
        var served = Allocation(ProcessPlacementMode.Cpu, new ResourceFootprint(GpuBytes: 0, 9 * Gb), window: 16384, slots: 2);

        await AssertServedVariantRefusedAsync(GpuVariant.Cuda, GpuVariant.Cpu, admitted, served);
    }

    [Test]
    public async Task OverBooking_ObservedTakeAboveBooking_LogsWarning()
    {
        var admitted = Allocation(ProcessPlacementMode.Hybrid, new ResourceFootprint(20 * Gb, 30 * Gb), window: 8192, slots: 1);
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        var registry = Admit(GpuVariant.Cuda, admitted, out var consumer);

        // 28 GiB free before the load, 6 GiB after: a 22 GiB take against a 20 GiB booking. Only fresh samples move, so a detection reading
        // the shared sample, which a real load finishes inside, sees no take.
        await using var supervisor = SupervisorFactory.Create(new FakeProcessLauncher(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchAdmissions: registry,
            logger: logger,
            liveMemorySampler: new SequenceSampler(28 * Gb, 6 * Gb));

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        var warning = SingleOverBookingWarning(logger);
        AssertEx.Contains(warning, "booked 20480 MiB");
        AssertEx.Contains(warning, "observed 22528 MiB");
        var health = AssertEx.NotNull((await supervisor.CheckHealthAsync(CancellationToken.None)).SingleOrDefault());
        AssertEx.False(health.HasExited, "the over-booked child keeps serving.");
        consumer.Dispose();
    }

    [Test]
    public async Task OverBooking_TakeWithinBooking_LogsNothing()
    {
        var admitted = Allocation(ProcessPlacementMode.Hybrid, new ResourceFootprint(20 * Gb, 30 * Gb), window: 8192, slots: 1);
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        var registry = Admit(GpuVariant.Cuda, admitted, out var consumer);
        await using var supervisor = SupervisorFactory.Create(new FakeProcessLauncher(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchAdmissions: registry,
            logger: logger,
            liveMemorySampler: new SequenceSampler(28 * Gb, 8 * Gb + 256 * Mib));

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Empty(OverBookingWarnings(logger));
        consumer.Dispose();
    }

    [Test]
    public async Task FailedFitMessage_LogsWarning()
    {
        var admitted = Allocation(ProcessPlacementMode.Hybrid, new ResourceFootprint(20 * Gb, 30 * Gb), window: 8192, slots: 1) with
        {
            FitTargetBytes = 2 * Gb
        };
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        var registry = Admit(GpuVariant.Cuda, admitted, out var consumer);
        var launcher = new FakeProcessLauncher
        {
            StartupLines = ["0.00.512.000 W common_fit_params: failed to fit params to free device memory: n_gpu_layers already set by user to 99, abort"]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            launchAdmissions: registry,
            logger: logger);

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        var warning = SingleOverBookingWarning(logger);
        AssertEx.Contains(warning, "fit failed: True");
        AssertEx.Contains(warning, "fit target 2048 MiB");
        consumer.Dispose();
    }

    private static string SingleOverBookingWarning(RecordingLogger<LlamaServerProcessSupervisor> logger)
    {
        var warnings = OverBookingWarnings(logger);
        AssertEx.Equal(expected: 1, warnings.Length);
        return warnings[0];
    }

    private static string[] OverBookingWarnings(RecordingLogger<LlamaServerProcessSupervisor> logger) =>
        logger.Entries
              .Where(static entry => entry.Level == LogLevel.Warning && entry.Message.Contains("may exceed its admitted GPU booking", StringComparison.Ordinal))
              .Select(static entry => entry.Message)
              .ToArray();

    private static async Task AssertServedVariantRefusedAsync(GpuVariant selected,
        GpuVariant servedVariant,
        ProcessContextAllocation admitted,
        ProcessContextAllocation served)
    {
        var launcher = new FakeProcessLauncher();
        var registry = Admit(selected, admitted, out var consumer);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(selected),
            allocationResolver: ResolverServing(served),
            launchAdmissions: registry,
            binaryManager: new FakeBinaryManager(servedVariant));

        var refused = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None));

        AssertEx.Contains(refused.Message, $"admitted for the {selected} runtime");
        AssertEx.Contains(refused.Message, $"the {servedVariant} runtime is served");
        AssertEx.Equal(expected: 0, launcher.LaunchCount);
        consumer.Dispose();
    }

    private static IProcessContextAllocationResolver ResolverServing(ProcessContextAllocation served)
    {
        var resolver = Substitute.For<IProcessContextAllocationResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<ResolvedLaunchArguments>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ProcessContextAllocation?>(served));
        return resolver;
    }

    private static ProcessLaunchAdmissionRegistry Admit(GpuVariant variant, ProcessContextAllocation allocation, out IProcessLaunchAdmissionLease consumer)
    {
        var registry = new ProcessLaunchAdmissionRegistry();
        consumer = AssertEx.NotNull(registry.Acquire(new ProcessLaunchAdmission
        {
            ModelName = Model,
            Role = ModelRole.Chat,
            Variant = variant,
            ResolvedArguments = ResolvedLaunchArguments.Explore(),
            Allocation = allocation
        }));
        return registry;
    }

    private static ProcessContextAllocation Allocation(ProcessPlacementMode placement, ResourceFootprint footprint, int window, int slots) =>
        new()
        {
            ProcessContextTokens = window,
            ModelTrainContextTokens = 131072,
            Source = ProcessContextAllocationSource.HardwareTier,
            Placement = placement,
            Footprint = footprint,
            ContentIdentity = $"{Model}:0",
            CacheKey = "cache:llama3",
            ChatSlots = slots
        };

    /// <summary>
    ///     Hands out one global free-VRAM reading per fresh call, in order, then repeats the last; a shared call returns the latest reading, as
    ///     the real sampler does inside its sharing window.
    /// </summary>
    private sealed class SequenceSampler : ILiveMemorySampler
    {
        private readonly long[] _availableVramBytes;
        private int _next;

        public SequenceSampler(params long[] availableVramBytes)
        {
            _availableVramBytes = availableVramBytes;
        }

        public Task<LiveMemorySample> SampleAsync(CancellationToken ct)
        {
            return Task.FromResult(Reading(Math.Max(Volatile.Read(ref _next) - 1, 0)));
        }

        public Task<LiveMemorySample> SampleFreshAsync(CancellationToken ct)
        {
            return Task.FromResult(Reading(Interlocked.Increment(ref _next) - 1));
        }

        private LiveMemorySample Reading(int call)
        {
            var index = Math.Min(call, _availableVramBytes.Length - 1);
            return new LiveMemorySample
            {
                TotalRamBytes = 64 * Gb,
                AvailableRamBytes = 48 * Gb,
                Gpus =
                [
                    new GpuMemorySample
                    {
                        Index = 0,
                        TotalVramBytes = 32 * Gb,
                        UsedVramBytes = 32 * Gb - _availableVramBytes[index],
                        AvailableVramBytes = _availableVramBytes[index]
                    }
                ]
            };
        }
    }
}
