namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class DevelopmentApplyRecoveryTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Test]
    public async Task CrashAfterHostMutation_SameKeyFinalizesWithoutApplyingTwice()
    {
        var port = new CrashAfterMutationApplyPort();
        await using var provider = await _fixture.BuildProviderAsync(port).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IDevelopmentCoordinator>();
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);
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
            SubjectHash: "subject");

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(operationId, subject, "repo")).ConfigureAwait(false);
        var completed = await coordinator.ApplyAsync(operationId, subject, "repo").ConfigureAwait(false);

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
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyAsync(operationId, subject, "repo")).ConfigureAwait(false);
        var completed = await coordinator.ApplyAsync(operationId, subject, "repo").ConfigureAwait(false);

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
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        var completed = await coordinator.ApplyAsync(operationId, subject, "repo").ConfigureAwait(false);
        var replay = await coordinator.ApplyAsync(operationId, subject, "repo").ConfigureAwait(false);

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
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var subject = CreateSubject(seed, version);

        var blocked = await coordinator.ApplyAsync(operationId, subject, "repo").ConfigureAwait(false);
        var replay = await coordinator.ApplyAsync(operationId, subject, "repo").ConfigureAwait(false);

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
        var (seed, version) = await SeedAwaitingApplyAsync(store).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<DevelopmentInvalidTransitionException>(() => store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                                                                                                      Guid.NewGuid(),
                                                                                                                      DevelopmentTaskStatus.Completed,
                                                                                                                      version)))
                      .ConfigureAwait(false);
    }

    private static DevelopmentApprovedApplySubject CreateSubject(DevelopmentCreateProjectCommand seed, long version)
        => new(seed.ProjectId,
            seed.TaskId,
            version,
            "base",
            "patch",
            "manifest",
            "result",
            "patch-ref",
            "manifest-ref",
            SubjectHash: "subject");

    private static async Task<(DevelopmentCreateProjectCommand Seed, long Version)> SeedAwaitingApplyAsync(IDevelopmentStore store)
    {
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        var version = 1L;
        foreach (var status in new[]
                 {
                     DevelopmentTaskStatus.Ready,
                     DevelopmentTaskStatus.InProgress,
                     DevelopmentTaskStatus.Validation,
                     DevelopmentTaskStatus.InReview,
                     DevelopmentTaskStatus.AwaitingApply
                 })
        {
            var result = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                                                         Guid.NewGuid(),
                                                                         status,
                                                                         version,
                                                                         ApprovedSubjectHash: status == DevelopmentTaskStatus.AwaitingApply
                                                                             ? "subject"
                                                                             : null))
                                    .ConfigureAwait(false);
            version = result.Version;
        }

        return (seed, version);
    }
}
