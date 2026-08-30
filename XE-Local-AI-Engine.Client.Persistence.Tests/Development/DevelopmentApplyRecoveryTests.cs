namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class DevelopmentApplyRecoveryTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() =>
        _fixture.Dispose();

    [Test]
    public async Task CrashAfterHostMutation_SameKeyFinalizesWithoutApplyingTwice()
    {
        var port = new CrashAfterMutationApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = new DevelopmentApprovedApplySubject(seed.ProjectId,
            seed.TaskId,
            version,
            "base",
            "patch",
            "manifest",
            "result",
            "patch-ref",
            "manifest-ref",
            SubjectHash: "subject",
            RepositoryIdentityHash: seed.RepositoryIdentityHash);

        var repository = Repository(seed);
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(operationId, subject, repository)).ConfigureAwait(false);
        var completed = await coordinator.ApplyAsync(operationId, subject, repository).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentOperationPhases.ApplyCompleted, completed.Phase);
        AssertEx.Equal(expected: 1, port.ApplyCalls);
        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyStarted));
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyCompleted));
    }

    [Test]
    public async Task CrashAfterApplyStartedBeforeHostMutation_RetryAppliesOnceAndCompletes()
    {
        var port = new CrashBeforeHostMutationApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        var repository = Repository(seed);
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(operationId, subject, repository)).ConfigureAwait(false);
        var completed = await coordinator.ApplyAsync(operationId, subject, repository).ConfigureAwait(false);

        AssertEx.Equal(DevelopmentOperationPhases.ApplyCompleted, completed.Phase);
        AssertEx.Equal(expected: 1, port.ApplyCalls);
        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyStarted));
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyCompleted));
    }

    [Test]
    public async Task CompletedApply_ResponseReplayDoesNotInspectOrMutateHostAgain()
    {
        var port = new CountingApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        var repository = Repository(seed);
        var completed = await coordinator.ApplyAsync(operationId, subject, repository).ConfigureAwait(false);
        var replay = await coordinator.ApplyAsync(operationId, subject, repository).ConfigureAwait(false);

        AssertEx.Equal(completed, replay);
        AssertEx.Equal(expected: 1, port.InspectCalls);
        AssertEx.Equal(expected: 1, port.ApplyCalls);
        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyStarted));
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyCompleted));
    }

    [Test]
    public async Task AmbiguousHostState_BlocksIdempotentlyWithoutApplying()
    {
        var port = new AmbiguousApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        var repository = Repository(seed);
        var blocked = await coordinator.ApplyAsync(operationId, subject, repository).ConfigureAwait(false);
        var replay = await coordinator.ApplyAsync(operationId, subject, repository).ConfigureAwait(false);

        AssertEx.Equal(blocked, replay);
        AssertEx.Equal(DevelopmentOperationPhases.ApplyBlocked, blocked.Phase);
        AssertEx.Equal(expected: 1, port.InspectCalls);
        AssertEx.Equal(expected: 0, port.ApplyCalls);
        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyStarted));
        AssertEx.Equal(expected: 1, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyBlocked));
        AssertEx.Equal(expected: 0, events.Count(item => item.OperationPhase == DevelopmentOperationPhases.ApplyCompleted));
    }

    [Test]
    public async Task GenericTransition_CannotBypassExplicitApplyCompletion()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var (seed, version) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(store).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                          Guid.NewGuid(),
                          DevelopmentTaskStatus.Completed,
                          version)))
                      .ConfigureAwait(false);
    }

    private static DevelopmentApprovedApplySubject CreateSubject(DevelopmentCreateProjectCommand seed, long version) =>
        new(seed.ProjectId,
            seed.TaskId,
            version,
            "base",
            "patch",
            "manifest",
            "result",
            "patch-ref",
            "manifest-ref",
            SubjectHash: "subject",
            RepositoryIdentityHash: seed.RepositoryIdentityHash);

    private static DevelopmentRepositoryBinding Repository(DevelopmentCreateProjectCommand seed) =>
        new(seed.ProjectId,
            seed.SelectedFolderId,
            "repository",
            "repo",
            seed.RepositoryIdentityHash);

}
