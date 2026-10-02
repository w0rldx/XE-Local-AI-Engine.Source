namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the supervisor respawns a resident daemon whose installed weight set changed (a Qwen-Image set gaining
///     its LlmVision part), fails closed while a job lease holds it, and ignores part order.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ImageServerSupervisorWeightSetTests
{
    private const string ModelName = "qwen-image";

    private static ImageModelPart Part(ImageModelPartRole role, string fileName) => new()
    {
        Role = role,
        FileName = fileName,
        LocalPath = "/fake/models/qwen/" + fileName,
        SizeBytes = 1024
    };

    private static readonly ImageModelPart Diffusion = Part(ImageModelPartRole.Diffusion, "diffusion.gguf");
    private static readonly ImageModelPart Vae = Part(ImageModelPartRole.Vae, "vae.safetensors");
    private static readonly ImageModelPart Llm = Part(ImageModelPartRole.Llm, "llm.gguf");
    private static readonly ImageModelPart LlmVision = Part(ImageModelPartRole.LlmVision, "mmproj.gguf");

    [Test]
    public async Task EnsureRunning_SameWeightSet_ReusesDaemon()
    {
        var launcher = new FakeImageProcessLauncher();
        var store = new FakeImageModelStore { Parts = [Diffusion, Vae, Llm] };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, modelStore: store);

        var first = await supervisor.EnsureRunningAsync(ModelName, CancellationToken.None);
        var second = await supervisor.EnsureRunningAsync(ModelName, CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.Equal(first.BaseAddress.AbsoluteUri, second.BaseAddress.AbsoluteUri);
    }

    [Test]
    public async Task EnsureRunning_SameWeightSetInDifferentOrder_ReusesDaemon()
    {
        var launcher = new FakeImageProcessLauncher();
        var store = new FakeImageModelStore { Parts = [Diffusion, Vae, Llm, LlmVision] };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, modelStore: store);

        await supervisor.EnsureRunningAsync(ModelName, CancellationToken.None);
        store.Parts = [LlmVision, Llm, Diffusion, Vae];
        await supervisor.EnsureRunningAsync(ModelName, CancellationToken.None);

        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        AssertEx.False(launcher.Handles.Single().WasTreeKilled);
    }

    [Test]
    public async Task EnsureRunning_InstallGainedLlmVisionPart_EvictsAndRespawnsWithLlmVision()
    {
        var launcher = new FakeImageProcessLauncher();
        var store = new FakeImageModelStore { Parts = [Diffusion, Vae, Llm] };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, modelStore: store);

        await supervisor.EnsureRunningAsync(ModelName, CancellationToken.None);
        var oldHandle = launcher.Handles.Single();
        AssertEx.False(launcher.Launches.Single().Arguments.Contains("--llm_vision"));

        store.Parts = [Diffusion, Vae, Llm, LlmVision];
        await supervisor.EnsureRunningAsync(ModelName, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(oldHandle.WasTreeKilled);
        var respawn = launcher.Launches.Last();
        var flag = respawn.Arguments.ToList().IndexOf("--llm_vision");
        AssertEx.True(flag >= 0);
        AssertEx.Equal(LlmVision.LocalPath, respawn.Arguments[flag + 1]);
        AssertEx.Equal(expected: 1, supervisor.GetResidents().Count);
    }

    [Test]
    public async Task EnsureRunning_WeightSetChangedUnderJobLease_FailsClosedAndLeavesDaemon()
    {
        var launcher = new FakeImageProcessLauncher();
        var store = new FakeImageModelStore { Parts = [Diffusion, Vae, Llm] };
        await using var supervisor = ImageSupervisorFactory.Create(launcher, modelStore: store);

        await supervisor.EnsureRunningAsync(ModelName, CancellationToken.None);
        using var lease = AssertEx.NotNull(supervisor.TryAcquireJobLease(ModelName));

        store.Parts = [Diffusion, Vae, Llm, LlmVision];
        var ex = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => supervisor.EnsureRunningAsync(ModelName, CancellationToken.None));

        AssertEx.True(ex.Message.Contains("busy", StringComparison.Ordinal));
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
        var oldHandle = launcher.Handles.Single();
        AssertEx.False(oldHandle.WasTreeKilled);
        AssertEx.False(oldHandle.HasExited);
        var resident = supervisor.GetResidents().Single();
        AssertEx.True(resident.HasActiveJobLease);
        AssertEx.Equal(ModelName, resident.ModelName);
    }
}
