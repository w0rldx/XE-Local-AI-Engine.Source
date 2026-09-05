namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GPU-load admission at the llama-server supervisor: two concurrent GPU-backed loads serialize their
///     spawn-through-readiness window through the shared gate (the second does not even launch until the first is
///     resident), while CPU-only loads bypass the gate entirely and launch concurrently.
/// </summary>
public sealed class SupervisorAdmissionTests
{
    [Test]
    public async Task EnsureParkedInReadiness_MutationLeaseObservesInflightAndReturnsNull()
    {
        var gatedProbe = new GatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(healthProbe: gatedProbe);
        var ensure = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        await AssertEx.EventuallyAsync(() => gatedProbe.Waiting == 1,
            TimeSpan.FromSeconds(5),
            "the spawn should be parked in readiness").ConfigureAwait(false);

        try
        {
            var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None)
                                        .WaitAsync(TimeSpan.FromSeconds(2))
                                        .ConfigureAwait(false);
            AssertEx.Null(lease);
        }
        finally
        {
            gatedProbe.Release();
            await ensure.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task TwoConcurrentGpuLoads_Serialize_SecondDoesNotLaunchUntilFirstReady()
    {
        var launcher = new FakeProcessLauncher();
        var gatedProbe = new GatedHealthProbe();
        using var admission = new GpuModelLoadAdmission(new GpuModelLoadAdmissionOptions());
        await using var supervisor = SupervisorFactory.Create(launcher,
            gatedProbe,
            variantSelector: new FakeVariantSelector(GpuVariant.Vulkan),
            loadAdmission: admission);

        var run1 = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var run2 = supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);

        // Exactly one GPU load reaches readiness (holding the gate); the other is blocked on admission and has NOT launched.
        await AssertEx.EventuallyAsync(() => gatedProbe.Waiting == 1, TimeSpan.FromSeconds(5),
            "one GPU load should be parked in readiness holding the admission gate").ConfigureAwait(false);
        await AssertEx.SettleAsync().ConfigureAwait(false);
        AssertEx.Equal(1, launcher.LaunchCount);

        // Release the first's readiness → it registers and releases the gate → the second is admitted and launches.
        gatedProbe.Release();
        await run1.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await run2.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.Equal(2, launcher.LaunchCount);
    }

    [Test]
    public async Task TwoConcurrentCpuLoads_DoNotQueue_BothLaunchImmediately()
    {
        var launcher = new FakeProcessLauncher();
        var gatedProbe = new GatedHealthProbe();
        // A real gate is injected, but a CPU variant must bypass it entirely — proving the gate never touches CPU loads.
        using var admission = new GpuModelLoadAdmission(new GpuModelLoadAdmissionOptions());
        await using var supervisor = SupervisorFactory.Create(launcher,
            gatedProbe,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            loadAdmission: admission);

        var run1 = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        var run2 = supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None);

        // Both CPU loads launch and park in readiness at once — no admission serialization.
        await AssertEx.EventuallyAsync(() => gatedProbe.Waiting == 2, TimeSpan.FromSeconds(5),
            "both CPU loads should launch concurrently (no admission gating)").ConfigureAwait(false);
        AssertEx.Equal(2, launcher.LaunchCount);

        gatedProbe.Release();
        await run1.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await run2.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
}
