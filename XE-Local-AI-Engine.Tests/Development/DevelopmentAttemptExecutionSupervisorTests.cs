namespace XE_Local_AI_Engine.Tests.Development;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Endpoints.Development.V1;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

[Category(TestCategories.Unit)]
public sealed class DevelopmentAttemptExecutionSupervisorTests
{
    [Test]
    public async Task DisposeAsync_WhenCalledRepeatedly_RemainsIdempotent()
    {
        var supervisor = new DevelopmentAttemptExecutionSupervisor(Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IDevelopmentAttemptLiveBroker>(),
            Substitute.For<IDevelopmentAttemptLiveEventPublisher>(),
            NullLogger<DevelopmentAttemptExecutionSupervisor>.Instance);

        await supervisor.DisposeAsync();
        var repeated = supervisor.DisposeAsync();

        // Without the guard the repeat would cancel an already-disposed source and fault.
        AssertEx.True(repeated.IsCompletedSuccessfully, "a repeated dispose is a synchronous no-op.");
        await repeated;
    }

    /// <summary>Validation has no attempt of its own, so the supervisor opens the judged coder attempt's stream and ends it with a Terminal.</summary>
    [Test]
    public async Task StartValidation_StreamsUnderTheJudgedCoderAttemptAndEndsWithATerminalUpdate()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var coderAttemptId = Guid.NewGuid();
        var broker = new DevelopmentAttemptLiveBroker(Options.Create(new DevelopmentOptions()), TimeProvider.System);
        var store = Substitute.For<IDevelopmentStore>();
        store.GetTaskAsync(taskId, Arg.Any<CancellationToken>()).Returns(DevelopmentEndpointTests.TaskAggregate(projectId, taskId, []).Task);
        store.ListAttemptsAsync(taskId, Arg.Any<CancellationToken>())
             .Returns([Attempt(Guid.NewGuid(), PersistenceDevelopmentAttemptStatus.Failed), Attempt(coderAttemptId, PersistenceDevelopmentAttemptStatus.Succeeded)]);
        var publisher = Substitute.For<IDevelopmentAttemptLiveEventPublisher>();
        var delivered = new List<DevelopmentAttemptLiveUpdate>();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.PublishAsync(Arg.Do<DevelopmentAttemptLiveUpdate>(update =>
                 {
                     // The delivery loop publishes one update at a time, so the list needs no lock.
                     delivered.Add(update);
                     if (update.Kind == DevelopmentAttemptLiveUpdateKind.Terminal)
                     {
                         terminal.TrySetResult();
                     }
                 }), Arg.Any<CancellationToken>())
                 .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(Substitute.For<IDevelopmentRepositoryBindingService>());
        services.AddSingleton<IDevelopmentValidationRunner>(new PublishingValidationRunner(broker, projectId, taskId, coderAttemptId));
        await using var provider = services.BuildServiceProvider();
        await using var supervisor = new DevelopmentAttemptExecutionSupervisor(provider.GetRequiredService<IServiceScopeFactory>(),
            broker,
            publisher,
            NullLogger<DevelopmentAttemptExecutionSupervisor>.Instance);

        AssertEx.True(supervisor.StartValidation(taskId));
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The run's own update, then the Terminal that tells a subscriber the validation stream ended.
        AssertEx.Equal("validation command|Validation passed.", string.Join('|', delivered.Select(static update => update.CurrentActivity)));
        AssertEx.True(delivered.All(update => update.AttemptId == coderAttemptId));
        AssertEx.Equal(DevelopmentAttemptLiveUpdateKind.Terminal, delivered[^1].Kind);
    }

    /// <summary>
    ///     Validation can start once the coder attempt commits Succeeded, before its run closes the coder stream: it waits for that
    ///     close, then streams under the same id with every update numbered above the coder run's Terminal.
    /// </summary>
    [Test]
    public async Task StartValidation_WhileTheCoderRunIsStillClosing_WaitsAndNumbersItsStreamAboveTheCoderTerminal()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var coderAttemptId = Guid.NewGuid();
        var broker = new DevelopmentAttemptLiveBroker(Options.Create(new DevelopmentOptions()), TimeProvider.System);
        var store = Substitute.For<IDevelopmentStore>();
        store.GetTaskAsync(taskId, Arg.Any<CancellationToken>()).Returns(DevelopmentEndpointTests.TaskAggregate(projectId, taskId, []).Task);
        store.GetExecutionSnapshotAsync(coderAttemptId, Arg.Any<CancellationToken>()).Returns(Execution(projectId, taskId, coderAttemptId));
        // Already committed Succeeded while the run is still held below.
        store.ListAttemptsAsync(taskId, Arg.Any<CancellationToken>()).Returns([Attempt(coderAttemptId, PersistenceDevelopmentAttemptStatus.Succeeded)]);
        var publisher = Substitute.For<IDevelopmentAttemptLiveEventPublisher>();
        var delivered = new ConcurrentQueue<DevelopmentAttemptLiveUpdate>();
        var validationEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.PublishAsync(Arg.Do<DevelopmentAttemptLiveUpdate>(update =>
                 {
                     delivered.Enqueue(update);
                     if (update.CurrentActivity == "Validation passed.")
                     {
                         validationEnded.TrySetResult();
                     }
                 }), Arg.Any<CancellationToken>())
                 .Returns(Task.CompletedTask);
        var coder = new HeldCoderRunner();
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(Substitute.For<IDevelopmentRepositoryBindingService>());
        services.AddSingleton<IDevelopmentCoderAttemptRunner>(coder);
        services.AddSingleton<IDevelopmentValidationRunner>(new PublishingValidationRunner(broker, projectId, taskId, coderAttemptId));
        await using var provider = services.BuildServiceProvider();
        await using var supervisor = new DevelopmentAttemptExecutionSupervisor(provider.GetRequiredService<IServiceScopeFactory>(),
            broker,
            publisher,
            NullLogger<DevelopmentAttemptExecutionSupervisor>.Instance);

        AssertEx.True(supervisor.StartAttempt(coderAttemptId, DevelopmentAttemptRole.Coder));
        await coder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        AssertEx.True(supervisor.StartValidation(taskId));
        coder.Release.SetResult();
        await validationEnded.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var updates = delivered.ToArray();
        var coderTerminal = updates.Single(static update => update.CurrentActivity == "Attempt finished.");
        var validationCommand = updates.Single(static update => update.CurrentActivity == "validation command");
        AssertEx.True(validationCommand.Sequence > coderTerminal.Sequence, "validation numbers its stream above the coder run's Terminal");
    }

    private static DevelopmentExecutionSnapshot Execution(Guid projectId, Guid taskId, Guid attemptId) =>
        new()
        {
            ProjectId = projectId,
            TaskId = taskId,
            AttemptId = attemptId,
            SelectedFolderId = Guid.NewGuid(),
            RepositoryIdentityHash = "identity",
            BaseBranch = "main",
            EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion = 1,
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = 1,
            TrustedRepositoryAcknowledgedAtUtc = 1,
            MaxTokens = 2048,
            MaxDurationSeconds = 60,
            Title = "Held coder",
            Requirements = "Hold the run.",
            AcceptanceCriteriaJson = "[]",
            TaskStatus = DevelopmentTaskStatus.InProgress,
            TaskVersion = 1,
            AttemptRole = DevelopmentAttemptRole.Coder,
            AttemptStatus = PersistenceDevelopmentAttemptStatus.Running,
            ModelId = "coder-model",
            Provider = "local",
            AttemptVersion = 1,
            CommandProfileJson = null
        };

    private static DevelopmentAttemptSnapshot Attempt(Guid attemptId, PersistenceDevelopmentAttemptStatus status) =>
        new()
        {
            Id = attemptId,
            TaskId = Guid.NewGuid(),
            PredecessorAttemptId = null,
            Role = DevelopmentAttemptRole.Coder,
            ModelId = "coder-model",
            Provider = "local",
            Status = status,
            StartedAtUtc = 1,
            EndedAtUtc = 2,
            TerminalReason = null,
            InputTokens = null,
            OutputTokens = null,
            Version = 1
        };

    /// <summary>A coder run held open after its attempt committed, until the test releases it.</summary>
    private sealed class HeldCoderRunner : IDevelopmentCoderAttemptRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DevelopmentCoderAttemptResult> RunAsync(Guid attemptId,
            DevelopmentRepositoryBinding repository,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return new DevelopmentCoderAttemptResult
            {
                AttemptId = attemptId,
                BaseCommit = "base",
                SubjectHash = "subject",
                PatchHash = "patch",
                ManifestHash = "manifest",
                ChangedFiles = []
            };
        }
    }

    /// <summary>Publishes one update under the coder attempt, which only reaches the publisher if the supervisor registered it.</summary>
    private sealed class PublishingValidationRunner : IDevelopmentValidationRunner
    {
        private readonly IDevelopmentAttemptLiveBroker _broker;
        private readonly Guid _projectId;
        private readonly Guid _taskId;
        private readonly Guid _attemptId;

        public PublishingValidationRunner(IDevelopmentAttemptLiveBroker broker, Guid projectId, Guid taskId, Guid attemptId)
        {
            _broker = broker;
            _projectId = projectId;
            _taskId = taskId;
            _attemptId = attemptId;
        }

        public Task<DevelopmentValidationResult> RunAsync(Guid taskId,
            DevelopmentRepositoryBinding repository,
            CancellationToken cancellationToken = default)
        {
            AssertEx.True(_broker.TryPublish(new DevelopmentAttemptLiveUpdate
            {
                ProjectId = _projectId,
                TaskId = _taskId,
                AttemptId = _attemptId,
                Kind = DevelopmentAttemptLiveUpdateKind.Command,
                Role = DevelopmentAttemptRole.Coder,
                Status = PersistenceDevelopmentAttemptStatus.Running,
                ModelId = "coder-model",
                Provider = "local",
                CurrentActivity = "validation command"
            }));
            return Task.FromResult(new DevelopmentValidationResult
            {
                ArtifactId = Guid.NewGuid(),
                Passed = true,
                TaskStatus = DevelopmentTaskStatus.InReview,
                SubjectHash = "subject"
            });
        }
    }
}
