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
                 .Returns(new TrainingSampleAppendResult(Sample: null, Duplicate: false));

        StructuredAgentRequest? request = null;
        var runner = Substitute.For<IStructuredAgentRunner>();
        _ = runner.RunAsync(Arg.Any<IChatClient>(), Arg.Do<StructuredAgentRequest>(value => request = value), Arg.Any<CancellationToken>())
                  .Returns(new StructuredAgentResult(Success: true, "{}", FailureReason: null));

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
        return new TrainingDatasetRecord(Guid.NewGuid(), Guid.NewGuid(), 1, definitionJson,
            "dataset", TrainingDatasetStatus.Generating, 1, null, 0, 0, 0, 0, 0, 1, 0, 0,
            DatasetGenerationWorkStatus.Running, null);
    }

    private static DatasetGenerationClaimedWork Work(TrainingDatasetRecord dataset) =>
        new(QueueSequence: 1, dataset.Id, dataset.Version, dataset);

    private static TrainingDefinitionRecord DefinitionRecord(DatasetDefinitionBodyV1 body) =>
        new(Guid.NewGuid(), "definition", TrainingDatasetKind.ToolCalling, JsonSerializer.SerializeToUtf8Bytes(body, TrainingJson.Options),
            DefinitionVersion: 2, Version: 2, CreatedAtUtc: 0, UpdatedAtUtc: 0);

    private static SampleValidationOutcome Accepted() =>
        new(Accepted: true, RejectionReason: null, TrainingSampleLabel.Good,
            new TrainingSampleContentV1
            {
                Parts = [new TrainingSamplePartV1("user", 0, "hi")]
            },
            new TrainingSampleValidationV1
            {
                Passed = true
            });

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
            await Task.CompletedTask.ConfigureAwait(false);
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
