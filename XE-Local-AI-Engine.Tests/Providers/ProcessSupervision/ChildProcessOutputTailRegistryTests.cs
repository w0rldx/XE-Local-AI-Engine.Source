namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Globalization;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The node-wide child-process output registry feeding the support bundle: bounded to the last eight processes,
///     exited ones evicted first, newest first, safe under concurrent launches.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ChildProcessOutputTailRegistryTests
{
    [Test]
    public void Snapshot_ListsNewestFirstWithStartAndExitTimes()
    {
        var clock = new ManualClock();
        var registry = new ChildProcessOutputTailRegistry(clock);

        var first = registry.Register("llama-server[a/chat]");
        first.Append("load_tensors: offloaded 25/25 layers to GPU");
        clock.Advance(TimeSpan.FromSeconds(1));
        _ = registry.Register("sd-server[sd15]");
        clock.Advance(TimeSpan.FromSeconds(1));
        registry.MarkExited(first);
        registry.MarkExited(first);

        var snapshot = registry.Snapshot();

        AssertEx.Equal(expected: 2, snapshot.Count);
        AssertEx.Equal("sd-server[sd15]", snapshot[0].Label);
        AssertEx.Null(snapshot[0].ExitedUtc, "a running process has no exit time");
        AssertEx.Equal("llama-server[a/chat]", snapshot[1].Label);
        AssertEx.Equal(ManualClock.Start, snapshot[1].StartedUtc);
        AssertEx.Equal<DateTimeOffset?>(ManualClock.Start.AddSeconds(2), snapshot[1].ExitedUtc, "a repeated MarkExited keeps the first stamp");
        AssertEx.Equal("load_tensors: offloaded 25/25 layers to GPU", snapshot[1].Lines.Single());
    }

    [Test]
    public void Register_PastTheCap_EvictsTheOldestExitedEntryFirst()
    {
        var registry = new ChildProcessOutputTailRegistry(new ManualClock());
        var tails = Enumerable.Range(0, ChildProcessOutputTailRegistry.MaxEntries)
            .Select(i => registry.Register(string.Create(CultureInfo.InvariantCulture, $"p{i}")))
            .ToArray();
        registry.MarkExited(tails[5]);
        registry.MarkExited(tails[3]);

        _ = registry.Register("p8");
        _ = registry.Register("p9");

        var labels = registry.Snapshot().Select(t => t.Label).ToArray();
        AssertEx.Equal("p9,p8,p7,p6,p4,p2,p1,p0", string.Join(',', labels), "the exited p3 and p5 go before any running entry");
    }

    [Test]
    public void Register_PastTheCapWithNothingExited_EvictsTheOldest()
    {
        var registry = new ChildProcessOutputTailRegistry(new ManualClock());
        for (var i = 0; i <= ChildProcessOutputTailRegistry.MaxEntries; i++)
        {
            _ = registry.Register(string.Create(CultureInfo.InvariantCulture, $"p{i}"));
        }

        var labels = registry.Snapshot().Select(t => t.Label).ToArray();
        AssertEx.Equal(ChildProcessOutputTailRegistry.MaxEntries, labels.Length);
        AssertEx.Equal("p1", labels[^1]);
    }

    [Test]
    public void Register_CreatesATailWithTheRegistryBounds()
    {
        var registry = new ChildProcessOutputTailRegistry(new ManualClock());
        var tail = registry.Register("llama-server[a/chat]");
        for (var i = 0; i <= ChildProcessOutputTailRegistry.TailMaxLines; i++)
        {
            tail.Append(string.Create(CultureInfo.InvariantCulture, $"line{i}"));
        }

        var lines = registry.Snapshot()[0].Lines;
        AssertEx.Equal(ChildProcessOutputTailRegistry.TailMaxLines, lines.Count);
        AssertEx.Equal("line1", lines[0]);
    }

    [Test]
    public async Task ConcurrentRegisterAppendAndSnapshot_StayWithinTheCap()
    {
        var registry = new ChildProcessOutputTailRegistry(new ManualClock());

        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                var tail = registry.Register(string.Create(CultureInfo.InvariantCulture, $"w{worker}-{i}"));
                tail.Append("line");
                registry.MarkExited(tail);
                _ = registry.Snapshot();
            }
        })));

        var snapshot = registry.Snapshot();
        AssertEx.Equal(ChildProcessOutputTailRegistry.MaxEntries, snapshot.Count);
        AssertEx.True(snapshot.All(t => t.ExitedUtc is not null && t.Lines.Count == 1));
    }

    private sealed class ManualClock : TimeProvider
    {
        public static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = Start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
