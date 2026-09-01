namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Adding a task to a project that already has one — the capability workflow decomposition needs, and the thing the
///     project-id unique index made impossible until it was widened.
/// </summary>
public sealed class DevelopmentCreateTaskTests : IDisposable
{
    private readonly DevelopmentTestFixture _fixture = new();

    public void Dispose() =>
        _fixture.Dispose();

    /// <summary>
    ///     Two tasks coexist under one project, each with its own row and its own status — the shape every other query
    ///     in this feature was already written for and the schema alone forbade.
    /// </summary>
    [Test]
    public async Task ASecondTask_LivesBesideTheFirstUnderTheSameProject()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);

        var created = await store.CreateTaskAsync(new DevelopmentCreateTaskCommand(seed.ProjectId,
                                      Guid.NewGuid(),
                                      Guid.NewGuid(),
                                      "Implement the second slice",
                                      "Do the other half.",
                                      "[\"the other half is done\"]",
                                      MaxReviewRounds: 5))
                                  .ConfigureAwait(false);

        var tasks = await store.ListTasksAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, tasks.Count, "A project carries as many tasks as its work was decomposed into.");
        AssertEx.True(tasks.All(task => task.ProjectId == seed.ProjectId), "Both tasks belong to the same project, which is what makes the trust decision carry.");
        AssertEx.Equal(seed.TaskId, tasks[0].Id, "The project's own task is still first: the list is ordered by creation.");

        var second = await store.GetTaskAsync(created.TaskId ?? throw new AssertionException("The create answered without naming the task it created.")).ConfigureAwait(false);
        AssertEx.Equal(DevelopmentTaskStatus.Planned, second.Status, "A task added later starts where a task created with its project starts.");
        AssertEx.Equal("Implement the second slice", second.Title);
        AssertEx.Equal("Do the other half.", second.Requirements);
        AssertEx.Equal("[\"the other half is done\"]", second.AcceptanceCriteriaJson);
        AssertEx.Equal(expected: 5, second.MaxReviewRounds, "The review budget is the caller's to set, exactly as it is at project creation.");
        AssertEx.Equal(expected: 1, second.Version);

        var events = await store.ListEventsAsync(seed.ProjectId).ConfigureAwait(false);
        AssertEx.True(events.Any(entry => entry.EventType == "TaskCreated" && entry.TaskId == created.TaskId),
            "The ledger records the task appearing, or the project's history has work in it that nothing accounts for.");
    }

    /// <summary>
    ///     The idempotency this exists for: a caller that crashes between creating the task and writing down which task
    ///     it created re-asks with the same operation identity and is handed the SAME one, rather than orphaning it and
    ///     starting a second implementation of the same work.
    /// </summary>
    [Test]
    public async Task ReplayingTheOperation_AnswersWithTheTaskItAlreadyCreated()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        var first = await store.CreateTaskAsync(new DevelopmentCreateTaskCommand(seed.ProjectId,
                                    Guid.NewGuid(),
                                    operationId,
                                    "Implement the second slice",
                                    "Do the other half.",
                                    "[\"the other half is done\"]"))
                                .ConfigureAwait(false);

        // A different task id, deliberately: the replay must answer with the task that exists, not create the one the
        // retry was about to ask for.
        var replay = await store.CreateTaskAsync(new DevelopmentCreateTaskCommand(seed.ProjectId,
                                     Guid.NewGuid(),
                                     operationId,
                                     "Implement the second slice",
                                     "Do the other half.",
                                     "[\"the other half is done\"]"))
                                 .ConfigureAwait(false);

        AssertEx.Equal(first.TaskId, replay.TaskId, "The same operation identity names the same task.");
        AssertEx.Equal(expected: 2,
            (await store.ListTasksAsync(seed.ProjectId).ConfigureAwait(false)).Count,
            "And it wrote no second row: an orphaned task is work nobody is driving.");
    }

    /// <summary>
    ///     Four children of one decomposition, creating their tasks at once in the SAME project — the shape the widening
    ///     made reachable and the one thing that was serialized before it.
    ///     <para>
    ///         What it pins is the OUTCOME: four tasks, four distinct ledger sequences, no caller answered a conflict.
    ///         The sequence is <c>MAX(sequence) + 1</c> under a unique index, so concurrent writers on one project can
    ///         compute the same number — and this passes with the store's re-run disabled as well, because SQLite's own
    ///         file lock serializes the read and the write together. That is worth writing down rather than dressing up:
    ///         the guard against the collision is in the store, and this is the assertion that the widening did not make
    ///         a project's own ledger the thing that refuses its work.
    ///     </para>
    /// </summary>
    [Test]
    public async Task FourChildrenCreatingTheirTasksAtOnce_AllGetOne()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var seedScope = provider.CreateAsyncScope();
        var seed = DevelopmentTestFixture.CreateSeed();
        _ = await seedScope.ServiceProvider.GetRequiredService<IDevelopmentStore>().CreateProjectAsync(seed).ConfigureAwait(false);

        // A scope each, because a DbContext is not shared; a barrier, so they really are inside the write at the same
        // moment rather than merely started together; and Task.Run, because an async lambda runs SYNCHRONOUSLY up to
        // its first await — the barrier would otherwise block the thread composing the second one and wait for three
        // callers that had not been created yet.
        using var start = new Barrier(participantCount: 4);
        var created = await Task.WhenAll(Enumerable.Range(1, 4)
                                                   .Select(index => Task.Run(async () =>
                                                   {
                                                       await using var scope = provider.CreateAsyncScope();
                                                       var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
                                                       _ = start.SignalAndWait(TimeSpan.FromSeconds(30));
                                                       return await store.CreateTaskAsync(new DevelopmentCreateTaskCommand(seed.ProjectId,
                                                                              Guid.NewGuid(),
                                                                              Guid.NewGuid(),
                                                                              $"Slice {index}",
                                                                              $"Implement slice {index}.",
                                                                              "[\"the slice is done\"]"))
                                                                          .ConfigureAwait(false);
                                                   })))
                                 .ConfigureAwait(false);

        await using var readScope = provider.CreateAsyncScope();
        var reader = readScope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        AssertEx.Equal(expected: 5, (await reader.ListTasksAsync(seed.ProjectId).ConfigureAwait(false)).Count, "the project's own task, plus one per child.");
        AssertEx.Equal(expected: 4,
            created.Select(static result => result.Sequence).Distinct().Count(),
            "each landed on a sequence of its own: the ledger is what the project's history is read from.");
    }

    /// <summary>
    ///     A task named against a project that does not exist is refused rather than written. The node connection runs
    ///     without foreign-key enforcement, so nothing below this would catch it and the row would simply be unreachable.
    /// </summary>
    [Test]
    public async Task ATaskForAProjectThatDoesNotExist_IsRefusedRatherThanOrphaned()
    {
        await using var provider = await _fixture.BuildProviderAsync().ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
        var missingProjectId = Guid.NewGuid();

        _ = await AssertEx.ThrowsAsync<KeyNotFoundException>(() => store.CreateTaskAsync(new DevelopmentCreateTaskCommand(missingProjectId,
                              Guid.NewGuid(),
                              Guid.NewGuid(),
                              "Implement the second slice",
                              "Do the other half.",
                              "[\"the other half is done\"]")))
                          .ConfigureAwait(false);

        AssertEx.Empty(await store.ListTasksAsync(missingProjectId).ConfigureAwait(false));
    }
}
