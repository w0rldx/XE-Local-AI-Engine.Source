namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class DevelopmentPersistenceTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() =>
        _fixture.Dispose();

    [Test]
    public async Task Model_ContainsExactlyFiveDevelopmentTablesAndRequiredUniqueIndexes()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var tables = dbContext.Model.GetEntityTypes()
                              .Select(entity => entity.GetTableName())
                              .Where(name => name?.StartsWith("development_", StringComparison.Ordinal) == true)
                              .Order(StringComparer.Ordinal)
                              .ToArray();

        AssertEx.Equal(expected: 5, tables.Length);
        AssertEx.True(tables.SequenceEqual([
                "development_artifacts",
                "development_attempts",
                "development_events",
                "development_projects",
                "development_tasks"
            ], StringComparer.Ordinal),
            "The Development schema must contain exactly the five approved persistence concepts.");

        var indexNames = dbContext.Model.GetEntityTypes()
                                  .SelectMany(entity => entity.GetIndexes())
                                  .Select(index => index.GetDatabaseName())
                                  .ToHashSet(StringComparer.Ordinal);
        AssertEx.True(indexNames.Contains("ux_development_attempts_one_active_per_task"));
        AssertEx.True(indexNames.Contains("ux_development_events_project_sequence"));
        AssertEx.True(indexNames.Contains("ux_development_events_operation_phase"));
    }

    [Test]
    public async Task Operations_AreIdempotentOrderedAndRejectStaleVersionsAndSecondActiveAttempt()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var acknowledgedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seed = DevelopmentTestFixture.CreateSeed() with
        {
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = 1,
            TrustedRepositoryAcknowledgedAtUtc = acknowledgedAt,
            MaxTokens = 4096,
            MaxDurationSeconds = 90
        };

        var created = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        var replay = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        AssertEx.Equal(created, replay, "The same operation key must reconstruct the original result.");

        var readyOperation = Guid.NewGuid();
        var ready = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                   readyOperation,
                                   DevelopmentTaskStatus.Ready,
                                   ExpectedTaskVersion: 1))
                               .ConfigureAwait(false);
        var readyReplay = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                         readyOperation,
                                         DevelopmentTaskStatus.Ready,
                                         ExpectedTaskVersion: 1))
                                     .ConfigureAwait(false);
        AssertEx.Equal(ready, readyReplay);

        await AssertEx.ThrowsAsync<DevelopmentConcurrencyException>(() => store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                          Guid.NewGuid(),
                          DevelopmentTaskStatus.InProgress,
                          ExpectedTaskVersion: 1)))
                      .ConfigureAwait(false);

        var attemptId = Guid.NewGuid();
        var firstAttempt = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                          attemptId,
                                          Guid.NewGuid(),
                                          DevelopmentAttemptRole.Coder,
                                          "local-model",
                                          "local",
                                          ExpectedTaskVersion: 2))
                                      .ConfigureAwait(false);
        AssertEx.Equal(DevelopmentAttemptStatus.Running.ToString(), firstAttempt.Status);
        var snapshot = await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false);
        AssertEx.Equal(seed.RepositoryIdentityHash, snapshot.RepositoryIdentityHash);
        AssertEx.Equal(seed.BaseBranch, snapshot.BaseBranch);
        AssertEx.True(snapshot.TrustedRepositoryAcknowledged);
        AssertEx.Equal(1, snapshot.TrustedRepositoryPolicyVersion);
        AssertEx.Equal(acknowledgedAt, snapshot.TrustedRepositoryAcknowledgedAtUtc);
        AssertEx.Equal(4096, snapshot.MaxTokens);
        AssertEx.Equal(90, snapshot.MaxDurationSeconds);
        AssertEx.Equal("local-model", snapshot.ModelId);

        await AssertEx.ThrowsAsync<DevelopmentConcurrencyException>(() => store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                          Guid.NewGuid(),
                          Guid.NewGuid(),
                          DevelopmentAttemptRole.Coder,
                          "local-model",
                          "local",
                          ExpectedTaskVersion: 3)))
                      .ConfigureAwait(false);

        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 3, events.Count);
        AssertEx.True(events.Select(item => item.Sequence).SequenceEqual([1L, 2L, 3L]));
    }
}
