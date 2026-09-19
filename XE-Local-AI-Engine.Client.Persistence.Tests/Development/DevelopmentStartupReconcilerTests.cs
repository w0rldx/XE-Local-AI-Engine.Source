namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class DevelopmentStartupReconcilerTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() =>
        _fixture.Dispose();

    [Test]
    public async Task ReconcileRunningAttempts_IsExactlyOnceAndLeavesOrderedInterruptionEvent()
    {
        await using var provider = await _fixture.BuildProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed);
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand { TaskId = seed.TaskId, OperationId = Guid.NewGuid(), TargetStatus = DevelopmentTaskStatus.Ready, ExpectedTaskVersion = 1 });
        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand
        {
            TaskId = seed.TaskId,
            AttemptId = attemptId,
            OperationId = Guid.NewGuid(),
            Role = DevelopmentAttemptRole.Coder,
            ModelId = "local-model",
            Provider = "local",
            ExpectedTaskVersion = 2
        });

        AssertEx.Equal(expected: 1, await store.ReconcileRunningAttemptsAsync("restart"));
        AssertEx.Equal(expected: 0, await store.ReconcileRunningAttemptsAsync("restart"));

        var events = await store.ListEventsAsync(seed.ProjectId);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == "AttemptInterrupted"));
        AssertEx.Equal(attemptId, events.Single(item => item.EventType == "AttemptInterrupted").AttemptId);
    }

    [Test]
    public async Task ReconcileRunningAttempts_ConcurrentCallsProduceOneTransitionAndOneEvent()
    {
        await using var provider = await _fixture.BuildProviderAsync();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var seedStore = seedScope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
            var seed = DevelopmentTestFixture.CreateSeed();
            _ = await seedStore.CreateProjectAsync(seed);
            _ = await seedStore.TransitionTaskAsync(new DevelopmentTransitionTaskCommand { TaskId = seed.TaskId, OperationId = Guid.NewGuid(), TargetStatus = DevelopmentTaskStatus.Ready, ExpectedTaskVersion = 1 });
            _ = await seedStore.StartAttemptAsync(new DevelopmentStartAttemptCommand
            {
                TaskId = seed.TaskId,
                AttemptId = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                Role = DevelopmentAttemptRole.Coder,
                ModelId = "local-model",
                Provider = "local",
                ExpectedTaskVersion = 2
            });

            await using var firstScope = provider.CreateAsyncScope();
            await using var secondScope = provider.CreateAsyncScope();
            var first = firstScope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ReconcileRunningAttemptsAsync("restart");
            var second = secondScope.ServiceProvider.GetRequiredService<IDevelopmentStore>().ReconcileRunningAttemptsAsync("restart");
            var results = await Task.WhenAll(first, second);

            AssertEx.Equal(expected: 1, results.Sum());
            var events = await seedStore.ListEventsAsync(seed.ProjectId);
            AssertEx.Equal(expected: 1, events.Count(item => item.EventType == "AttemptInterrupted"));
        }
    }
}
