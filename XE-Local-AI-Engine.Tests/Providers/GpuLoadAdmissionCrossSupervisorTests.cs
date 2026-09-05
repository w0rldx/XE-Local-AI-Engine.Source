namespace XE_Local_AI_Engine.Tests.Providers;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Cross-supervisor serialization: an image (stable-diffusion.cpp) load and an LLM (llama-server) load share
///     ONE process-wide GPU-load admission gate, so a GPU-backed image spawn does not begin while a GPU-backed LLM spawn
///     holds the gate — the two never race two <c>--fit</c> / free-VRAM reads.
/// </summary>
public sealed class GpuLoadAdmissionCrossSupervisorTests
{
    [Test]
    public async Task ImageAndLlmGpuLoads_SerializeAgainstEachOther_ViaSharedGate()
    {
        using var admission = new GpuModelLoadAdmission(new GpuModelLoadAdmissionOptions());

        // LLM: a Vulkan (GPU) load whose readiness we hold, so it keeps the shared gate.
        var llmLauncher = new FakeProcessLauncher();
        var llmProbe = new GatedHealthProbe();
        await using var llm = SupervisorFactory.Create(llmLauncher,
            llmProbe,
            variantSelector: new FakeVariantSelector(GpuVariant.Vulkan),
            loadAdmission: admission);

        // Image: a Vulkan (GPU) load sharing the SAME gate; its readiness is immediate.
        var imageLauncher = new FakeImageProcessLauncher();
        await using var image = ImageSupervisorFactory.Create(launcher: imageLauncher,
            readinessProbe: new FakeImageReadinessProbe(),
            backendSelector: new FakeSdBackendSelector(SdGpuBackend.Vulkan),
            binaryManager: new FakeSdBinaryManager(SdGpuBackend.Vulkan),
            loadAdmission: admission);

        var llmRun = llm.EnsureRunningAsync("llm-model", ModelRole.Chat, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => llmProbe.Waiting == 1, TimeSpan.FromSeconds(5),
            "the LLM load should launch and hold the shared gate").ConfigureAwait(false);

        // The image load competes for the SAME gate — it must not launch while the LLM holds it.
        var imageRun = image.EnsureRunningAsync("image-model", CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(imageRun, "The image load must stay queued while the LLM holds the shared gate.").ConfigureAwait(false);
        AssertEx.Equal(0, imageLauncher.LaunchCount);

        // Release the LLM's readiness → it releases the gate → the image load is admitted and launches.
        llmProbe.Release();
        await llmRun.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await imageRun.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.Equal(1, imageLauncher.LaunchCount);
        AssertEx.Equal(1, llmLauncher.LaunchCount);
    }
}
