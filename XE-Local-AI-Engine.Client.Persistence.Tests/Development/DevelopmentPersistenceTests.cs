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
    public async Task Model_ContainsExactlyTheApprovedDevelopmentTablesAndRequiredUniqueIndexes()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var tables = dbContext.Model.GetEntityTypes()
                              .Select(entity => entity.GetTableName())
                              .Where(name => name?.StartsWith("development_", StringComparison.Ordinal) == true)
                              .Order(StringComparer.Ordinal)
                              .ToArray();

        AssertEx.Equal(expected: 7, tables.Length);
        AssertEx.True(tables.SequenceEqual([
                "development_artifacts",
                "development_attempts",
                "development_events",
                "development_projects",
                "development_tasks",
                "development_template_materializations",
                "development_templates"
            ], StringComparer.Ordinal),
            "The Development schema must contain exactly the approved persistence concepts.");

        var indexNames = dbContext.Model.GetEntityTypes()
                                  .SelectMany(entity => entity.GetIndexes())
                                  .Select(index => index.GetDatabaseName())
                                  .ToHashSet(StringComparer.Ordinal);
        AssertEx.True(indexNames.Contains("ux_development_attempts_one_active_per_task"));
        AssertEx.True(indexNames.Contains("ux_development_events_project_sequence"));
        AssertEx.True(indexNames.Contains("ux_development_events_operation_phase"));
    }

    /// <summary>
    ///     S1.5.1. An attempt freezes the command profile it runs under, so editing the project's profile afterwards
    ///     cannot retroactively change what a historical attempt is judged against — and a reviewer attempt inherits
    ///     the coder attempt's profile rather than picking up the edit, which is what keeps one evidence chain under
    ///     one profile.
    /// </summary>
    [Test]
    public async Task AttemptCommandProfile_IsFrozenAtCreationAndInheritedByTheReviewerOfThatCoderAttempt()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        const string ProfileAtAttemptTime = """{"profileId":"generic-git","profileVersion":"v1"}""";
        const string ProfileAfterEdit = """{"profileId":"dotnet-slnx","profileVersion":"v1"}""";

        var seed = DevelopmentTestFixture.CreateSeed() with { CommandProfileJson = ProfileAtAttemptTime };
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.Ready,
                           ExpectedTaskVersion: 1))
                       .ConfigureAwait(false);

        var coderAttemptId = Guid.NewGuid();
        var coder = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                                    coderAttemptId,
                                    Guid.NewGuid(),
                                    DevelopmentAttemptRole.Coder,
                                    "local-model",
                                    "local",
                                    ExpectedTaskVersion: 2))
                                .ConfigureAwait(false);

        AssertEx.Equal(ProfileAtAttemptTime,
            (await store.GetExecutionSnapshotAsync(coderAttemptId).ConfigureAwait(false)).CommandProfileJson,
            "A new attempt must snapshot the project's profile as it stands at creation.");

        _ = await store.TerminalizeAttemptAsync(new DevelopmentTerminalizeAttemptCommand(coderAttemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptStatus.Succeeded,
                           ExpectedAttemptVersion: coder.Version))
                       .ConfigureAwait(false);

        // The profile edit. There is no operator-facing edit path yet; this is the write one would perform, and it is
        // the exact event that made the project row stop being a safe stand-in for the attempt's profile.
        var project = await dbContext.DevelopmentProjects.SingleAsync(entity => entity.Id == seed.ProjectId).ConfigureAwait(false);
        project.CommandProfileJson = ProfileAfterEdit;
        project.Version++;
        _ = await dbContext.SaveChangesAsync().ConfigureAwait(false);

        AssertEx.Equal(ProfileAtAttemptTime,
            (await store.GetExecutionSnapshotAsync(coderAttemptId).ConfigureAwait(false)).CommandProfileJson,
            "Editing the project's profile must not retroactively change what a historical attempt ran under.");

        // InProgress reaches InReview only through Validation; the transition table has no direct edge.
        async Task<long> TaskVersionAsync() =>
            await dbContext.DevelopmentTasks.AsNoTracking()
                           .Where(entity => entity.Id == seed.TaskId)
                           .Select(entity => entity.Version)
                           .SingleAsync()
                           .ConfigureAwait(false);

        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.Validation,
                           await TaskVersionAsync().ConfigureAwait(false)))
                       .ConfigureAwait(false);
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.InReview,
                           await TaskVersionAsync().ConfigureAwait(false)))
                       .ConfigureAwait(false);

        var reviewerAttemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           reviewerAttemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Reviewer,
                           "local-model",
                           "local",
                           await TaskVersionAsync().ConfigureAwait(false)))
                       .ConfigureAwait(false);

        AssertEx.Equal(ProfileAtAttemptTime,
            (await store.GetExecutionSnapshotAsync(reviewerAttemptId).ConfigureAwait(false)).CommandProfileJson,
            "A reviewer must review under the profile the coder attempt ran, not one edited in since.");
    }

    /// <summary>
    ///     An attempt row that predates the attempt-level column reads the project's profile, so the column's
    ///     introduction changes nothing for history already on disk.
    /// </summary>
    [Test]
    public async Task AttemptCommandProfile_FallsBackToTheProjectWhenTheAttemptPredatesTheColumn()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        const string ProjectProfile = """{"profileId":"generic-git","profileVersion":"v1"}""";
        var seed = DevelopmentTestFixture.CreateSeed() with { CommandProfileJson = ProjectProfile };
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        _ = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                           Guid.NewGuid(),
                           DevelopmentTaskStatus.Ready,
                           ExpectedTaskVersion: 1))
                       .ConfigureAwait(false);

        var attemptId = Guid.NewGuid();
        _ = await store.StartAttemptAsync(new DevelopmentStartAttemptCommand(seed.TaskId,
                           attemptId,
                           Guid.NewGuid(),
                           DevelopmentAttemptRole.Coder,
                           "local-model",
                           "local",
                           ExpectedTaskVersion: 2))
                       .ConfigureAwait(false);

        // Reproduce a row written before the column existed.
        var attempt = await dbContext.DevelopmentAttempts.SingleAsync(entity => entity.Id == attemptId).ConfigureAwait(false);
        attempt.CommandProfileJson = null;
        _ = await dbContext.SaveChangesAsync().ConfigureAwait(false);

        AssertEx.Equal(ProjectProfile,
            (await store.GetExecutionSnapshotAsync(attemptId).ConfigureAwait(false)).CommandProfileJson,
            "An attempt with no snapshot must resolve the project's profile, exactly as it did before this column.");
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
