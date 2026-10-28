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
        using var hold = AssertEx.NotNull(gate.TryBeginExclusive(kind));

        var notice = AssertEx.NotNull(GpuWorkHolderNotice.ForModelLoad(gate, Guid.NewGuid()));

        AssertEx.Equal(TurnNoticeKind.QueuedBehindGpuWork, notice.Kind);
        AssertEx.Equal(message, notice.Message);
        AssertEx.Null(notice.Detail, "The message names the holder; a raw id is no detail line.");
    }

    [Test]
    public void ForModelLoad_OnlySharedHolders_SaysNothing()
    {
        var gate = new GpuWorkGate();
        using var benchmark = AssertEx.NotNull(gate.TryBeginShared(GpuWorkKind.Benchmark));
        using var image = AssertEx.NotNull(gate.TryBeginShared(GpuWorkKind.ImageJob));

        AssertEx.Null(GpuWorkHolderNotice.ForModelLoad(gate, Guid.NewGuid()));
        AssertEx.Equal("Benchmark, ImageJob", string.Join(", ", gate.Holders));
    }
}
