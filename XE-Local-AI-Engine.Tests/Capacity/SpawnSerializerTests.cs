namespace XE_Local_AI_Engine.Tests.Capacity;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="SpawnSerializer" /> tests: same-model serialization with a bounded wait. Two same-model runs do not
///     overlap (they serialize on the one process); a run that cannot acquire its turn within the timeout returns the
///     "busy" fallback rather than hanging; and distinct models do not block each other.
/// </summary>
public sealed class SpawnSerializerTests
{
    private const string Model = "bartowski/Model-GGUF:Q4_K_M";

    [Test]
    public async Task RunSerialized_SameModel_DoesNotOverlap()
    {
        var serializer = new SpawnSerializer();
        var concurrent = 0;
        var maxConcurrent = 0;
        var sync = new Lock();

        async Task<string> Body(CancellationToken ct)
        {
            lock (sync)
            {
                concurrent++;
                maxConcurrent = Math.Max(maxConcurrent, concurrent);
            }

            // Suspends the body at an await, exactly where a sleep did, so a wrongly-admitted sibling would get to run
            // and be counted — without spending 50 ms of wall clock to prove it.
            await AssertEx.SettleAsync();

            lock (sync)
            {
                concurrent--;
            }

            return "done";
        }

        var runs = Enumerable.Range(0, 4)
                             .Select(_ => serializer.RunSerializedAsync(Model, ModelRole.Chat, TimeSpan.FromSeconds(10), Body, static () => "busy", CancellationToken.None))
                             .ToArray();
        await Task.WhenAll(runs);

        // The single-slot semaphore means no two same-model bodies ever ran at once.
        AssertEx.Equal(1, maxConcurrent);
    }

    [Test]
    public async Task RunSerialized_WhenWaitTimesOut_ReturnsBusy_NotHang()
    {
        var serializer = new SpawnSerializer();
        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Hold the model's turn open so the second waiter cannot acquire it within its short timeout.
        var holder = serializer.RunSerializedAsync(Model,
            ModelRole.Chat,
            TimeSpan.FromSeconds(10),
            async _ =>
            {
                holderEntered.SetResult();
                await releaseHolder.Task;
                return "holder";
            },
            static () => "busy",
            CancellationToken.None);

        await holderEntered.Task;

        var waiterRan = false;
        var waiter = await serializer.RunSerializedAsync(Model,
            ModelRole.Chat,
            TimeSpan.FromMilliseconds(100),
            _ =>
            {
                waiterRan = true;
                return Task.FromResult("waiter");
            },
            static () => "busy",
            CancellationToken.None);

        AssertEx.Equal("busy", waiter);
        AssertEx.False(waiterRan, "the run body must not execute when the bounded wait times out");

        releaseHolder.SetResult();
        _ = await holder;
    }

    [Test]
    public async Task RunSerialized_DifferentModels_DoNotBlockEachOther()
    {
        var serializer = new SpawnSerializer();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = serializer.RunSerializedAsync("model-a:Q4_K_M",
            ModelRole.Chat,
            TimeSpan.FromSeconds(10),
            async _ =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
                return "a";
            },
            static () => "busy",
            CancellationToken.None);

        await firstEntered.Task;

        // A different model acquires its own gate immediately despite the first being held.
        var second = await serializer.RunSerializedAsync("model-b:Q4_K_M",
            ModelRole.Chat,
            TimeSpan.FromMilliseconds(200),
            static _ => Task.FromResult("b"),
            static () => "busy",
            CancellationToken.None);

        AssertEx.Equal("b", second);

        releaseFirst.SetResult();
        _ = await first;
    }
}
