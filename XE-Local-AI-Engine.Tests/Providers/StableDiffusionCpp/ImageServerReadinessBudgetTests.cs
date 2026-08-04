namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     sd-server binds its socket only after the synchronous model load finishes, so the readiness wait IS the load
///     wait — which makes a flat budget an assumption about model size. The flat two minutes that comfortably covers a
///     ~2 GB SD1.5 file fails an ~18 GB Qwen-Image set on its FIRST launch, and the operator is told the runtime "did
///     not become ready in time", which reads as a broken model rather than an impatient budget.
/// </summary>
public sealed class ImageServerReadinessBudgetTests
{
    [Test]
    public void For_SmallFileSet_KeepsTheConfiguredFloor()
    {
        var options = new StableDiffusionRuntimeOptions();

        var budget = ImageServerReadinessBudget.For([Part(2L * 1024 * 1024 * 1024)], options);

        AssertEx.Equal(options.ReadinessTimeout, budget, "A file-set that loads inside the floor must not shorten it.");
    }

    [Test]
    public void For_LargeFileSet_ScalesTheBudgetWithTheSetTotal()
    {
        // The real case: 13 GB diffusion transformer + 4.7 GB LLM text encoder + 0.25 GB VAE.
        var options = new StableDiffusionRuntimeOptions();

        var budget = ImageServerReadinessBudget.For(
            [Part(13_065_746_976), Part(4_683_072_512), Part(253_806_246)],
            options);

        AssertEx.True(budget > options.ReadinessTimeout, "An 18 GB set must get more than the flat floor.");
        var expectedSeconds = 18_002_625_734d / options.ReadinessLoadBytesPerSecond;
        AssertEx.True(Math.Abs(budget.TotalSeconds - expectedSeconds) < 1.0,
            $"Expected ~{expectedSeconds:F0}s scaled from the set total, got {budget.TotalSeconds:F0}s.");
    }

    [Test]
    public void For_AbsurdlyLargeSet_IsCappedSoAFailedSpawnStillFails()
    {
        var options = new StableDiffusionRuntimeOptions();

        var budget = ImageServerReadinessBudget.For([Part(long.MaxValue / 2)], options);

        AssertEx.Equal(options.MaxReadinessTimeout, budget, "A corrupt size must never make a doomed spawn hang forever.");
    }

    [Test]
    public void For_PartsWithoutSizes_FallsBackToTheFloorRatherThanZero()
    {
        // A registry written before sizes were recorded reports none. Deriving zero from that would turn every spawn
        // into an instant readiness failure — strictly worse than the flat budget it replaced.
        var options = new StableDiffusionRuntimeOptions();

        var budget = ImageServerReadinessBudget.For([Part(0), Part(-1)], options);

        AssertEx.Equal(options.ReadinessTimeout, budget);
    }

    [Test]
    public void For_ScalingDisabled_KeepsTheFlatFloor()
    {
        var options = new StableDiffusionRuntimeOptions { ReadinessLoadBytesPerSecond = 0 };

        var budget = ImageServerReadinessBudget.For([Part(18_002_625_734)], options);

        AssertEx.Equal(options.ReadinessTimeout, budget);
    }

    private static ImageModelPart Part(long sizeBytes)
    {
        return new ImageModelPart
        {
            Role = ImageModelPartRole.Diffusion,
            FileName = "weights.gguf",
            LocalPath = "/models/weights.gguf",
            SizeBytes = sizeBytes
        };
    }
}
