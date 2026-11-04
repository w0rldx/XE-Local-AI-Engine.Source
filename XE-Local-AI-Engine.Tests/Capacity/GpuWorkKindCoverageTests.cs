namespace XE_Local_AI_Engine.Tests.Capacity;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Both readers of the exclusive holder end in <c>_ => null</c>, so a new kind compiles with no chat notice and no update guard unless this fails.</summary>
[Category(TestCategories.Unit)]
public sealed class GpuWorkKindCoverageTests
{
    /// <summary>The kinds that only ever take the shared hold, so neither reader can see them. A new kind joins this list only if that is true of it.</summary>
    private static readonly GpuWorkKind[] SharedOnly = [GpuWorkKind.Benchmark, GpuWorkKind.DatasetGeneration, GpuWorkKind.ImageJob];

    [Test]
    public async Task EveryExclusiveKind_HasAChatNoticeAndAnUpdateBusyRow()
    {
        foreach (var kind in Enum.GetValues<GpuWorkKind>().Except(SharedOnly))
        {
            var gate = new GpuWorkGate();
            AssertEx.Succeeded(gate.TryBeginExclusive(kind, out var hold));
            using var holdScope = hold;
            AssertEx.NotNull(GpuWorkHolderNotice.ForModelLoad(gate, Guid.NewGuid()), $"{kind} has no row in GpuWorkHolderNotice.ForModelLoad.");

            var sources = new AppUpdateBusySources();
            sources.GpuWorkGate.ExclusiveKind.Returns(kind);
            var items = await sources.Probe().CollectAsync(CancellationToken.None);
            AssertEx.Equal(expected: 1, items.Count, $"{kind} has no row in AppUpdateBusyProbe.CollectAsync.");
        }
    }
}
