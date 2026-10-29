namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Which exclusive GPU job a model load names, over the real gate.</summary>
[Category(TestCategories.Unit)]
public sealed class GpuWorkHolderNoticeTests
{
    [Test]
    [Arguments(GpuWorkKind.TrainingRun, "This message was queued behind a running training run.")]
    [Arguments(GpuWorkKind.EvaluationRun, "This message was queued behind a running training evaluation.")]
    [Arguments(GpuWorkKind.Export, "This message was queued behind a running model export.")]
    public void ForModelLoad_AnExclusiveHolder_NamesIt(GpuWorkKind kind, string message)
    {
        var gate = new GpuWorkGate();
        AssertEx.Succeeded(gate.TryBeginExclusive(kind, out var hold));
        using var holdScope = hold;

        var notice = AssertEx.NotNull(GpuWorkHolderNotice.ForModelLoad(gate, Guid.NewGuid()));

        AssertEx.Equal(TurnNoticeKind.QueuedBehindGpuWork, notice.Kind);
        AssertEx.Equal(message, notice.Message);
        AssertEx.Null(notice.Detail, "The message names the holder; a raw id is no detail line.");
    }

    [Test]
    public void ForModelLoad_OnlySharedHolders_SaysNothing()
    {
        var gate = new GpuWorkGate();
        AssertEx.Succeeded(gate.TryBeginShared(GpuWorkKind.Benchmark, out var benchmark));
        using var benchmarkScope = benchmark;
        AssertEx.Succeeded(gate.TryBeginShared(GpuWorkKind.ImageJob, out var image));
        using var imageScope = image;

        AssertEx.Null(GpuWorkHolderNotice.ForModelLoad(gate, Guid.NewGuid()));
        AssertEx.Equal("Benchmark, ImageJob", string.Join(", ", gate.Holders));
    }
}
