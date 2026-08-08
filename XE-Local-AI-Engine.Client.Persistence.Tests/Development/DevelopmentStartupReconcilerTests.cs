namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class DevelopmentStartupReconcilerTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() =>
        _fixture.Dispose();

    [Test]
    public async Task ReconcileRunningAttempts_IsExactlyOnceAndLeavesOrderedInterruptionEvent()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId, Guid.NewGuid(), DevelopmentTaskStatus.Ready, 1)).ConfigureAwait(false);
        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           attemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           ExpectedTaskVersion: 2))
                       .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, await store.ReconcileRunningAttemptsAsync("restart").ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await store.ReconcileRunningAttemptsAsync("restart").ConfigureAwait(false));

        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == "AttemptInterrupted"));
        AssertEx.Equal(attemptId, events.Single(item => item.EventType == "AttemptInterrupted").AttemptId);
    }

    [Test]
    public async Task ReconcileRunningAttempts_ConcurrentCallsProduceOneTransitionAndOneEvent()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedStore = seedScope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
            var seed = DevelopmentTestFixture.CreateSeed();
            _ = await seedStore.CreateProjectAsync(seed).ConfigureAwait(false);
            _ = await seedStore.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId, Guid.NewGuid(), DevelopmentTaskStatus.Ready, 1)).ConfigureAwait(false);
            _ = await seedStore.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                   Guid.NewGuid(),
                                   Guid.NewGuid(),
                                   DevelopmentAttemptRole.Coder,
                                   "local-model",
                                   "local",
                                   ExpectedTaskVersion: 2))
                               .ConfigureAwait(false);

            await using var firstScope = provider.CreateAsyncScope();
            await using var secondScope = provider.CreateAsyncScope();
            var first = firstScope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ReconcileRunningAttemptsAsync("restart");
            var second = secondScope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ReconcileRunningAttemptsAsync("restart");
            var results = await Task.WhenAll(first, second).ConfigureAwait(false);

            AssertEx.Equal(expected: 1, results.Sum());
            var events = await seedStore.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
            AssertEx.Equal(expected: 1, events.Count(item => item.EventType == "AttemptInterrupted"));
        }
    }
}
