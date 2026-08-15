namespace XE_Local_AI_Engine.Tests.Training.Evaluation;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins that an evaluation scores against the definition body the DATASET pinned, not the live definition row. The
///     tool offers and the system instructions are the whole question an evaluation asks, so a definition edited after
///     generation would otherwise score the model against tools the dataset never demonstrated.
/// </summary>
public sealed class EvaluationRunExecutorTests
{
    private static readonly Guid DatasetId = Guid.NewGuid();
    private static readonly Guid SampleId = Guid.NewGuid();

    [Test]
    public async Task Evaluation_OffersThePinnedToolSnapshot_NotTheEditedLiveOne()
    {
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>())
                    .Returns(Dataset(Body("PINNED INSTRUCTIONS", "pinned_tool")));

        // The live definition has since been edited. Reading it here is the bug under test.
        _ = datasets.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(DefinitionRecord(Body("LIVE INSTRUCTIONS", "live_tool")));
        _ = datasets.ListAllSamplesAsync(DatasetId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TrainingSampleRecord>>([Sample()]);

        var evaluation = Evaluation();
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.AppendResultsAsync(evaluation.Id, Arg.Any<IReadOnlyList<TrainingEvaluationResultEntry>>(), Arg.Any<CancellationToken>())
                 .Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(evaluation);

        using var client = new RecordingChatClient();
        var executor = new EvaluationRunExecutor(store, datasets, Resolver(client), Substitute.For<ITrainingRunEventBuffer>(),
            new TrainingRunCancellationRegistry(), NullLogger<EvaluationRunExecutor>.Instance);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        AssertEx.Equal("pinned_tool", AssertEx.NotNull(client.LastOptions, "The model must have been asked one question.").Tools!.Single().Name);
        AssertEx.Contains(client.LastSystemMessage ?? string.Empty, "PINNED INSTRUCTIONS");
        _ = await datasets.DidNotReceiveWithAnyArgs().GetDefinitionAsync(Guid.Empty, default);
        _ = await store.Received(1).CompleteAsync(evaluation.Id, TrainingWorkStatus.Succeeded, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Evaluation_WhenTheDatasetPredatesPinning_IsRejectedWithAReason()
    {
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(pinnedBody: null));
        _ = datasets.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(DefinitionRecord(Body("LIVE INSTRUCTIONS", "live_tool")));

        var evaluation = Evaluation();
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(evaluation);

        using var client = new RecordingChatClient();
        var executor = new EvaluationRunExecutor(store, datasets, Resolver(client), Substitute.For<ITrainingRunEventBuffer>(),
            new TrainingRunCancellationRegistry(), NullLogger<EvaluationRunExecutor>.Instance);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        _ = await store.Received(1).CompleteAsync(evaluation.Id,
            TrainingWorkStatus.Failed,
            DatasetDefinitionService.UnpinnedDatasetReason,
            Arg.Any<CancellationToken>());
        AssertEx.Null(client.LastOptions, "No model may be consulted when the body being scored against is unknown.");
    }

    private static DatasetDefinitionBodyV1 Body(string instructions, string toolName) =>
        new()
        {
            TeacherModelName = "teacher.gguf",
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
        return new TrainingDatasetRecord(DatasetId, Guid.NewGuid(), 1, definitionJson, "dataset", TrainingDatasetStatus.Ready, 1,
            "v1:" + new string('a', count: 64), 1, 1, 0, 0, 0, 1, 0, 0, DatasetGenerationWorkStatus.Succeeded, null);
    }

    private static TrainingDefinitionRecord DefinitionRecord(DatasetDefinitionBodyV1 body) =>
        new(Guid.NewGuid(), "definition", TrainingDatasetKind.ToolCalling, JsonSerializer.SerializeToUtf8Bytes(body, TrainingJson.Options),
            DefinitionVersion: 2, Version: 2, CreatedAtUtc: 0, UpdatedAtUtc: 0);

    private static TrainingSampleRecord Sample()
    {
        var content = new TrainingSampleContentV1
        {
            Parts =
            [
                new TrainingSamplePartV1("user", 0, "call the tool"),
                new TrainingSamplePartV1("tool", 1, ToolName: "pinned_tool", Arguments: "{}")
            ]
        };
        return new TrainingSampleRecord(SampleId, DatasetId, 0, "tool-call", TrainingSampleLabel.Good, TrainingSampleReviewState.Approved,
            JsonSerializer.SerializeToUtf8Bytes(content, TrainingJson.Options), ValidationJson: null, TrainingSampleProvenance.Generated,
            new string('a', count: 64), CreatedAtUtc: 0, UpdatedAtUtc: 0);
    }

    private static TrainingEvaluationRecord Evaluation()
    {
        var membership = new TrainingEvaluationMembershipV1
        {
            TrainingRunId = Guid.NewGuid(),
            FreezeId = Guid.NewGuid(),
            DatasetId = DatasetId,
            DatasetContentFingerprint = "v1:" + new string('a', count: 64),
            HoldoutSampleIds = [SampleId]
        };
        return new TrainingEvaluationRecord(Guid.NewGuid(),
            TrainingRunId: null,
            ComparisonId: null,
            "tuned-model",
            ModelContentFingerprint: null,
            DatasetId,
            membership.DatasetContentFingerprint,
            JsonSerializer.SerializeToUtf8Bytes(membership, TrainingJson.Options),
            // Already Running, so the executor scores without a transition the fake store would have to model.
            TrainingEvaluationStatus.Running,
            ResultsJson: null,
            TotalCount: 1,
            ScoredCount: 0,
            PassedCount: 0,
            PerKindJson: null,
            ErrorMessage: null,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            TrainingWorkStatus.Running);
    }

    private static TrainingWorkClaim Claim(Guid evaluationId) =>
        new(QueueSequence: 1, TrainingWorkKind.EvaluationRun, evaluationId, Version: 1, Run: null);

    private static ILocalModelProviderResolver Resolver(IChatClient client)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        _ = provider.ProviderName.Returns("llamacpp");
        _ = provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(client);

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        _ = resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(provider);
        return resolver;
    }

    /// <summary>Records the offers and the system turn the executor composed; it never calls anything back.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }

        public string? LastSystemMessage { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            LastSystemMessage = messages.FirstOrDefault(message => message.Role == ChatRole.System)?.Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "no call")));
        }

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
