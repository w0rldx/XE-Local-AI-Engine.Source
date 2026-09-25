namespace XE_Local_AI_Engine.Tests.Training;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins that a dataset generates against the definition body it PINNED at creation. Editing a definition between a
///     dataset's creation and its generation used to swap the teacher, the tool snapshot and the instructions while the
///     dataset still claimed the older <c>DefinitionVersion</c>.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DatasetGenerationExecutorTests
{
    [Test]
    public async Task Generation_ReadsThePinnedDefinition_NotTheEditedLiveOne()
    {
        var store = Substitute.For<ITrainingDatasetStore>();

        // The live definition row has since been edited to a different teacher, tool set and instructions. Nothing in
        // the generation path may observe it.
        _ = store.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                 .Returns(DefinitionRecord(Body("live-teacher.gguf", "LIVE INSTRUCTIONS", "live_tool")));
        _ = store.AppendSampleAsync(Arg.Any<TrainingSampleInput>(), Arg.Any<CancellationToken>())
                 .Returns(new TrainingSampleAppendResult
                 {
                     Sample = null,
                     Duplicate = false
                 });

        StructuredAgentRequest? request = null;
        var runner = Substitute.For<IStructuredAgentRunner>();
        _ = runner.RunAsync(Arg.Any<IChatClient>(), Arg.Do<StructuredAgentRequest>(value => request = value), Arg.Any<CancellationToken>())
                  .Returns(new StructuredAgentResult
                  {
                      Success = true,
                      Text = "{}",
                      FailureReason = null
                  });

        SampleValidationContext? validationContext = null;
        var pipeline = Substitute.For<ISampleValidationPipeline>();
        _ = pipeline.ValidateAsync(Arg.Any<string>(), Arg.Do<SampleValidationContext>(value => validationContext = value), Arg.Any<CancellationToken>())
                    .Returns(Accepted());

        var requestedModels = new List<string>();
        var executor = new DatasetGenerationExecutor(store, runner, pipeline, Resolver(requestedModels), Events(),
            new TrainingRunCancellationRegistry(), NullLogger<DatasetGenerationExecutor>.Instance);

        await executor.ExecuteAsync(Work(Dataset(Body("pinned-teacher.gguf", "PINNED INSTRUCTIONS", "pinned_tool"))), CancellationToken.None);

        var teacherTurn = AssertEx.NotNull(request, "The teacher turn must have been composed.");
        AssertEx.Equal("pinned-teacher.gguf", teacherTurn.ModelName);
        AssertEx.Contains(teacherTurn.SystemInstructions, "PINNED INSTRUCTIONS");
        AssertEx.Contains(teacherTurn.SystemInstructions, "pinned_tool");
        AssertEx.False(teacherTurn.SystemInstructions.Contains("live_tool", StringComparison.Ordinal),
            "The edited definition's tool snapshot must never reach the teacher.");

        AssertEx.Equal("pinned-teacher.gguf", requestedModels.Single());
        AssertEx.Equal("pinned-teacher.gguf",
            AssertEx.NotNull(validationContext, "The pipeline is handed the definition it validates against.").Definition.TeacherModelName);

        _ = await store.DidNotReceiveWithAnyArgs().GetDefinitionAsync(Guid.Empty, default);
        _ = await store.Received(1).CompleteGenerationAsync(Arg.Any<Guid>(), DatasetGenerationWorkStatus.Succeeded, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Generation_WhenTheDatasetPredatesPinning_FailsWithAReason()
    {
        var store = Substitute.For<ITrainingDatasetStore>();
        _ = store.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                 .Returns(DefinitionRecord(Body("live-teacher.gguf", "LIVE INSTRUCTIONS", "live_tool")));
        var runner = Substitute.For<IStructuredAgentRunner>();

        var executor = new DatasetGenerationExecutor(store, runner, Substitute.For<ISampleValidationPipeline>(), Resolver([]), Events(),
            new TrainingRunCancellationRegistry(), NullLogger<DatasetGenerationExecutor>.Instance);

        await executor.ExecuteAsync(Work(Dataset(pinnedBody: null)), CancellationToken.None);

        // Falling back to the live definition would be exactly the silent re-shaping the pin exists to prevent, so the
        // dataset fails with an operator-facing reason instead.
        _ = await store.Received(1).CompleteGenerationAsync(Arg.Any<Guid>(),
            DatasetGenerationWorkStatus.Failed,
            DatasetDefinitionService.UnpinnedDatasetReason,
            Arg.Any<CancellationToken>());
        _ = await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default);
    }

    [Test]
    public async Task Generation_WithAnExternalTeacher_FailsTheRunWithoutBuildingAClient()
    {
        // Guarded at the EXECUTOR as well as in the runner: this is the seam that resolves a provider and constructs
        // the chat client, so reaching it with an ext: id would open a live connection to the external endpoint before
        // the runner's own guard ever saw the first turn.
        var store = Substitute.For<ITrainingDatasetStore>();
        var requestedModels = new List<string>();
        var executor = new DatasetGenerationExecutor(store,
            Substitute.For<IStructuredAgentRunner>(),
            Substitute.For<ISampleValidationPipeline>(),
            Resolver(requestedModels),
            Events(),
            new TrainingRunCancellationRegistry(),
            NullLogger<DatasetGenerationExecutor>.Instance);

        await executor.ExecuteAsync(Work(Dataset(Body("ext:local-box/qwen3", "instructions", "tool"))), CancellationToken.None);

        AssertEx.Empty(requestedModels);
        _ = await store.Received(1).CompleteGenerationAsync(Arg.Any<Guid>(),
            DatasetGenerationWorkStatus.Failed,
            Arg.Is<string>(reason => reason.Contains("external model", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Generation_WithAnExternalCritic_FailsTheRun()
    {
        // The critic never passes through the teacher runner, so the executor is the ONLY place its model is checked.
        var store = Substitute.For<ITrainingDatasetStore>();
        var body = Body("teacher.gguf", "instructions", "tool") with
        {
            CriticEnabled = true,
            CriticModelName = "ext:local-box/qwen3"
        };
        var executor = new DatasetGenerationExecutor(store,
            Substitute.For<IStructuredAgentRunner>(),
            Substitute.For<ISampleValidationPipeline>(),
            Resolver([]),
            Events(),
            new TrainingRunCancellationRegistry(),
            NullLogger<DatasetGenerationExecutor>.Instance);

        await executor.ExecuteAsync(Work(Dataset(body)), CancellationToken.None);

        _ = await store.Received(1).CompleteGenerationAsync(Arg.Any<Guid>(),
            DatasetGenerationWorkStatus.Failed,
            Arg.Is<string>(reason => reason.Contains("external model", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Generation_ThatRejectsEverySample_FailsWithTheCountsAndReasons_NotReady()
    {
        // F-57: a reasoning teacher had every sample rejected and the dataset still read Ready / Succeeded with zero samples.
        var store = Substitute.For<ITrainingDatasetStore>();
        var runner = Substitute.For<IStructuredAgentRunner>();
        _ = runner.RunAsync(Arg.Any<IChatClient>(), Arg.Any<StructuredAgentRequest>(), Arg.Any<CancellationToken>())
                  .Returns(new StructuredAgentResult
                           {
                               Success = true,
                               Text = "{}",
                               FailureReason = null
                           },
                      new StructuredAgentResult
                      {
                          Success = true,
                          Text = "{}",
                          FailureReason = null
                      },
                      new StructuredAgentResult
                      {
                          Success = false,
                          Text = string.Empty,
                          FailureReason = "The teacher returned an empty completion."
                      });
        var pipeline = Substitute.For<ISampleValidationPipeline>();
        _ = pipeline.ValidateAsync(Arg.Any<string>(), Arg.Any<SampleValidationContext>(), Arg.Any<CancellationToken>())
                    .Returns(new SampleValidationOutcome
                    {
                        Accepted = false,
                        RejectionReason = "Required property 'userMessage' is missing.",
                        Label = TrainingSampleLabel.Good,
                        Content = null,
                        Validation = new TrainingSampleValidationV1
                        {
                            Passed = false
                        }
                    });
        var body = Body("teacher.gguf", "instructions", "tool") with
        {
            SampleKinds = [new DatasetSampleKindTargetV1("rivers", Count: 3, TrainingSampleLabel.Good)]
        };
        var executor = new DatasetGenerationExecutor(store, runner, pipeline, Resolver([]), Events(),
            new TrainingRunCancellationRegistry(), NullLogger<DatasetGenerationExecutor>.Instance);

        await executor.ExecuteAsync(Work(Dataset(body)), CancellationToken.None);

        _ = await store.Received(1).CompleteGenerationAsync(Arg.Any<Guid>(),
            DatasetGenerationWorkStatus.Failed,
            "No usable samples: all 3 generated samples were rejected. Most frequent reasons: Required property 'userMessage' is missing. (2x); The teacher returned an empty completion. (1x)",
            Arg.Any<CancellationToken>());
        _ = await store.DidNotReceive().CompleteGenerationAsync(Arg.Any<Guid>(), DatasetGenerationWorkStatus.Succeeded, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await store.Received(3).RecordRejectedSampleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OperatorCancel_AfterTheLastRejection_EndsCancelled_NotFailed()
    {
        // Reviewer-found: the zero-sample branch could emit a "Failed" hub event and then store Cancelled.
        var store = Substitute.For<ITrainingDatasetStore>();
        var registry = new TrainingRunCancellationRegistry();
        var dataset = Dataset(Body("teacher.gguf", "instructions", "tool"));
        var runner = Substitute.For<IStructuredAgentRunner>();
        _ = runner.RunAsync(Arg.Any<IChatClient>(), Arg.Any<StructuredAgentRequest>(), Arg.Any<CancellationToken>())
                  .Returns(_ =>
                  {
                      AssertEx.True(registry.Cancel(dataset.Id), "the generation must be registered while its turn runs.");
                      return new StructuredAgentResult
                      {
                          Success = false,
                          Text = string.Empty,
                          FailureReason = "x"
                      };
                  });
        var executor = new DatasetGenerationExecutor(store, runner, Substitute.For<ISampleValidationPipeline>(), Resolver([]), Events(),
            registry, NullLogger<DatasetGenerationExecutor>.Instance);

        await executor.ExecuteAsync(Work(dataset), CancellationToken.None);

        _ = await store.Received(1).CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Cancelled, null, Arg.Any<CancellationToken>());
        _ = await store.DidNotReceive().CompleteGenerationAsync(Arg.Any<Guid>(), DatasetGenerationWorkStatus.Failed, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OperatorCancel_DuringTheFailedCommit_PublishesNoFailedEvent()
    {
        // Codex-found: a cancel landing inside the store commit left a live "Failed" event over a stored Cancelled.
        var store = Substitute.For<ITrainingDatasetStore>();
        var registry = new TrainingRunCancellationRegistry();
        var dataset = Dataset(Body("teacher.gguf", "instructions", "tool"));
        _ = store.CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Failed, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns<TrainingDatasetRecord>(_ =>
                 {
                     AssertEx.True(registry.Cancel(dataset.Id), "the generation must be registered while it commits.");
                     throw new OperationCanceledException();
                 });
        var runner = Substitute.For<IStructuredAgentRunner>();
        _ = runner.RunAsync(Arg.Any<IChatClient>(), Arg.Any<StructuredAgentRequest>(), Arg.Any<CancellationToken>())
                  .Returns(new StructuredAgentResult
                  {
                      Success = false,
                      Text = string.Empty,
                      FailureReason = "x"
                  });
        var events = Events();
        var published = new List<DatasetGenerationEvent>();
        events.EventPublished += (_, args) => published.Add(args.Event);
        var executor = new DatasetGenerationExecutor(store, runner, Substitute.For<ISampleValidationPipeline>(), Resolver([]), events,
            registry, NullLogger<DatasetGenerationExecutor>.Instance);

        await executor.ExecuteAsync(Work(dataset), CancellationToken.None);

        _ = await store.Received(1).CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Cancelled, null, Arg.Any<CancellationToken>());
        AssertEx.False(published.Any(item => item.Payload.State == nameof(TrainingDatasetStatus.Failed)),
            "the hub must never announce an outcome the store did not keep.");
    }

    [Test]
    public async Task TheComposedTeacherPrompt_IsWhatTheEchoCheckRecognises()
    {
        // The echo check is derived from the prompt template; this pins that the two cannot drift apart.
        var store = Substitute.For<ITrainingDatasetStore>();
        StructuredAgentRequest? request = null;
        var runner = Substitute.For<IStructuredAgentRunner>();
        _ = runner.RunAsync(Arg.Any<IChatClient>(), Arg.Do<StructuredAgentRequest>(value => request = value), Arg.Any<CancellationToken>())
                  .Returns(new StructuredAgentResult
                  {
                      Success = false,
                      Text = string.Empty,
                      FailureReason = "x"
                  });
        var executor = new DatasetGenerationExecutor(store, runner, Substitute.For<ISampleValidationPipeline>(), Resolver([]), Events(),
            new TrainingRunCancellationRegistry(), NullLogger<DatasetGenerationExecutor>.Instance);

        await executor.ExecuteAsync(Work(Dataset(Body("teacher.gguf", "instructions", "tool"))), CancellationToken.None);

        var prompt = AssertEx.NotNull(request).UserPrompt;
        AssertEx.True(DatasetGenerationExecutor.EchoesTeacherPrompt(prompt), prompt);
        AssertEx.True(DatasetGenerationExecutor.EchoesTeacherPrompt(prompt[..(prompt.IndexOf(". ", StringComparison.Ordinal) + 1)]),
            "The first sentence alone is the echo shape seen live.");
    }

    private static DatasetDefinitionBodyV1 Body(string teacher, string instructions, string toolName) =>
        new()
        {
            TeacherModelName = teacher,
            TeacherOutputMode = TeacherOutputMode.ValidateAfter,
            SystemInstructions = instructions,
            Tools = [new DatasetToolSnapshotV1(toolName, "does a thing", """{"type":"object"}""", RequiresApproval: false, ToolCategory.ReadLocal)],
            SampleKinds = [new DatasetSampleKindTargetV1("tool-call", Count: 1, TrainingSampleLabel.Good)]
        };

    /// <summary>A null body is a dataset created before pinning existed — the only way the column reads as absent.</summary>
    private static TrainingDatasetRecord Dataset(DatasetDefinitionBodyV1? pinnedBody)
    {
        ReadOnlyMemory<byte>? definitionJson = pinnedBody is null
            ? null
            : new ReadOnlyMemory<byte>(JsonSerializer.SerializeToUtf8Bytes(pinnedBody, TrainingJson.Options));
        return new TrainingDatasetRecord
        {
            Id = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
            DefinitionVersion = 1,
            DefinitionJson = definitionJson,
            Name = "dataset",
            Status = TrainingDatasetStatus.Generating,
            Revision = 1,
            ContentFingerprint = null,
            TotalSampleCount = 0,
            GoodSampleCount = 0,
            BadSampleCount = 0,
            RejectedSampleCount = 0,
            DuplicateSampleCount = 0,
            Version = 1,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0,
            WorkStatus = DatasetGenerationWorkStatus.Running,
            WorkErrorMessage = null
        };
    }

    private static DatasetGenerationClaimedWork Work(TrainingDatasetRecord dataset) =>
        new()
        {
            QueueSequence = 1,
            DatasetId = dataset.Id,
            Version = dataset.Version,
            Dataset = dataset
        };

    private static TrainingDefinitionRecord DefinitionRecord(DatasetDefinitionBodyV1 body) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "definition",
            Kind = TrainingDatasetKind.ToolCalling,
            DefinitionJson = JsonSerializer.SerializeToUtf8Bytes(body, TrainingJson.Options),
            DefinitionVersion = 2,
            Version = 2,
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0
        };

    private static SampleValidationOutcome Accepted() =>
        new()
        {
            Accepted = true,
            RejectionReason = null,
            Label = TrainingSampleLabel.Good,
            Content = new TrainingSampleContentV1
            {
                Parts = [new TrainingSamplePartV1("user", 0, "hi")]
            },
            Validation = new TrainingSampleValidationV1
            {
                Passed = true
            }
        };

    private static DatasetGenerationEventBuffer Events() =>
        new(Options.Create(new DatasetGenerationEventBufferOptions()));

    /// <summary>Records every model name the executor asked to resolve; the teacher it reaches is the pin under test.</summary>
    private static ILocalModelProviderResolver Resolver(List<string> requestedModels)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        _ = provider.ProviderName.Returns("llamacpp");
        _ = provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(_ => new StubChatClient());

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        _ = resolver.ResolveProviderForModelAsync(Arg.Do<string>(requestedModels.Add), Arg.Any<CancellationToken>()).Returns(provider);
        return resolver;
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
