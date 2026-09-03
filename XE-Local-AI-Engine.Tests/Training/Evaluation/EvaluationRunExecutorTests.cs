namespace XE_Local_AI_Engine.Tests.Training.Evaluation;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Training.Runs;

/// <summary>
///     Pins that an evaluation scores against the definition body the DATASET pinned, not the live definition row. The
///     tool offers and the system instructions are the whole question an evaluation asks, so a definition edited after
///     generation would otherwise score the model against tools the dataset never demonstrated. Sample trajectories
///     come from the run-owned immutable corpus, so a later live review edit cannot change an evaluation already queued.
/// </summary>
public sealed class EvaluationRunExecutorTests : IDisposable
{
    private static readonly Guid DatasetId = Guid.NewGuid();
    private static readonly Guid SampleId = Guid.NewGuid();

    /// <summary>What the run froze and the membership carries; later live-dataset changes are irrelevant to replay.</summary>
    private static readonly string FrozenFingerprint = "v1:" + new string('a', count: 64);

    private readonly FixedNodeSqliteKeyHolder _keyHolder = new(RandomNumberGenerator.GetBytes(32));
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Evaluation_OffersThePinnedToolSnapshot_NotTheEditedLiveOne()
    {
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>())
                    .Returns(Dataset(Body("PINNED INSTRUCTIONS", "pinned_tool")));

        // The live definition has since been edited. Reading it here is the bug under test.
        _ = datasets.GetDefinitionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(DefinitionRecord(Body("LIVE INSTRUCTIONS", "live_tool")));
        var evaluation = Evaluation();
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.AppendResultsAsync(evaluation.Id, Arg.Any<IReadOnlyList<TrainingEvaluationResultEntry>>(), Arg.Any<CancellationToken>())
                 .Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(evaluation);

        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store, datasets, client, evaluation);

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
        var executor = await CreateExecutorAsync(store, datasets, client, evaluation);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        _ = await store.Received(1).CompleteAsync(evaluation.Id,
            TrainingWorkStatus.Failed,
            DatasetDefinitionService.UnpinnedDatasetReason,
            Arg.Any<CancellationToken>());
        AssertEx.Null(client.LastOptions, "No model may be consulted when the body being scored against is unknown.");
    }

    /// <summary>
    ///     A sample edited between evaluation creation and claim (or before resume) moves the live dataset, but the
    ///     already-created evaluation must continue to answer the exact frozen questions it was queued against.
    /// </summary>
    [Test]
    public async Task Evaluation_WhenTheLiveDatasetDrifts_ReplaysTheImmutableFrozenCorpus()
    {
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>())
                    .Returns(Dataset(Body("PINNED INSTRUCTIONS", "pinned_tool"), "v1:" + new string('b', count: 64)));
        var evaluation = Evaluation();
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.AppendResultsAsync(evaluation.Id, Arg.Any<IReadOnlyList<TrainingEvaluationResultEntry>>(), Arg.Any<CancellationToken>())
                 .Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(evaluation);

        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store, datasets, client, evaluation);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        _ = await store.Received(1).CompleteAsync(evaluation.Id, TrainingWorkStatus.Succeeded, null, Arg.Any<CancellationToken>());
        AssertEx.NotNull(client.LastOptions, "Evaluation must replay the run-owned freeze even when live review state moved later.");
        AssertEx.Equal(expected: 2048, client.LastOptions!.MaxOutputTokens!.Value);
        _ = await datasets.DidNotReceiveWithAnyArgs().ListAllSamplesAsync(Guid.Empty, default);
        _ = await store.Received(1).AppendResultsAsync(evaluation.Id, Arg.Any<IReadOnlyList<TrainingEvaluationResultEntry>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Evaluation_ProviderFailure_IsPersistedAsSanitizedVerdict()
    {
        const string secret = "https://provider.invalid/?token=secret";
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var evaluation = Evaluation();
        TrainingEvaluationResultEntry? verdict = null;
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.AppendResultsAsync(evaluation.Id, Arg.Do<IReadOnlyList<TrainingEvaluationResultEntry>>(items => verdict = items.Single()),
            Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(evaluation);
        using var client = new ThrowingChatClient(new HttpRequestException(secret));
        var executor = await CreateExecutorAsync(store, datasets, client, evaluation);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        AssertEx.Equal("The local model provider could not complete this sample.", AssertEx.NotNull(verdict).Reason!);
        AssertEx.False(verdict!.Reason!.Contains(secret, StringComparison.Ordinal));
    }

    [Test]
    [Arguments(FrozenFixtureMode.Corrupt, "The immutable training corpus failed its integrity check.")]
    [Arguments(FrozenFixtureMode.FreezeMismatch, "The evaluation membership does not name the training run's frozen corpus.")]
    [Arguments(FrozenFixtureMode.MissingHoldout, "The immutable training corpus does not contain every frozen hold-out sample.")]
    public async Task Evaluation_InvalidFrozenCorpus_FailsBeforeConsultingTheModel(FrozenFixtureMode mode, string expectedReason)
    {
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var evaluation = Evaluation();
        string? persistedReason = null;
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, TrainingWorkStatus.Failed, Arg.Do<string?>(reason => persistedReason = reason),
            Arg.Any<CancellationToken>()).Returns(evaluation);
        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store, datasets, client, evaluation, mode);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        AssertEx.Equal(expectedReason, persistedReason!);
        AssertEx.Null(client.LastOptions, "Corpus validation must finish before model construction or inference.");
    }

    [Test]
    public async Task Evaluation_LegacyV1Corpus_ReplaysThroughTheSequenceToIdMigration()
    {
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var evaluation = Evaluation();
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.AppendResultsAsync(evaluation.Id, Arg.Any<IReadOnlyList<TrainingEvaluationResultEntry>>(), Arg.Any<CancellationToken>())
                 .Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(evaluation);
        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store, datasets, client, evaluation, FrozenFixtureMode.LegacyV1);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        AssertEx.NotNull(client.LastOptions);
        _ = await store.Received(1).CompleteAsync(evaluation.Id, TrainingWorkStatus.Succeeded, null, Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Evaluation_HarnessOrClientConstructionFailure_IsSanitizedBeforePersistenceAndEvents(bool failDuringClientConstruction)
    {
        const string secret = "Authorization: Bearer resolver-secret";
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var evaluation = Evaluation();
        string? persistedReason = null;
        TrainingRunPayload? published = null;
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, TrainingWorkStatus.Failed, Arg.Do<string?>(reason => persistedReason = reason),
            Arg.Any<CancellationToken>()).Returns(evaluation);
        var events = Substitute.For<ITrainingRunEventBuffer>();
        _ = events.Append(Arg.Any<Guid>(), TrainingRunEventKind.EvaluationState, Arg.Do<TrainingRunPayload>(payload => published = payload));
        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store,
            datasets,
            client,
            evaluation,
            FrozenFixtureMode.Current,
            events,
            harnessFailure: failDuringClientConstruction ? null : new HttpRequestException(secret),
            clientFactoryFailure: failDuringClientConstruction ? new HttpRequestException(secret) : null);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        AssertEx.Equal("The evaluation run failed.", persistedReason!);
        AssertEx.Equal("The evaluation run failed.", AssertEx.NotNull(published).Message!);
        AssertEx.False(persistedReason!.Contains(secret, StringComparison.Ordinal));
        AssertEx.False(published!.Message!.Contains(secret, StringComparison.Ordinal));
    }

    [Test]
    public async Task Evaluation_StagedArtifact_UsesControlledHarnessBeforeRegistryActivation()
    {
        var path = Path.Combine(_root, "tuned.gguf");
        _ = Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, "gguf");
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("gguf")));
        var artifactId = Guid.NewGuid();
        var evaluation = Evaluation() with
        {
            ModelName = "tuned.gguf",
            ModelContentFingerprint = sha,
            TargetKind = EvaluationModelTargetKind.StagedTrainingArtifact,
            SourceArtifactId = artifactId
        };
        var artifact = new TrainingArtifactRecord(artifactId, evaluation.TrainingRunId!.Value, TrainingArtifactKind.MergedGguf,
            path, sha, 4, TrainingArtifactSmokeState.Passed, null, null, 2, 0, 0);
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.AppendResultsAsync(evaluation.Id, Arg.Any<IReadOnlyList<TrainingEvaluationResultEntry>>(), Arg.Any<CancellationToken>())
                 .Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(evaluation);
        using var client = new RecordingChatClient();
        var factory = Substitute.For<IInferenceChatClientFactory>();
        _ = factory.CreateChatClient(Arg.Any<Uri>(), Arg.Any<string>()).Returns(client);
        var harness = EvaluationHarness();
        var executor = await CreateExecutorAsync(store, datasets, client, evaluation, artifact: artifact, evaluationHarness: harness,
            chatClientFactory: factory);

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        _ = await harness.Received(1).RunAsync(Arg.Is<TransientLlamaServerEvaluationRequest>(request => request.ModelFilePath == path
                                                                                                        && request.AdapterFilePath == null
                                                                                                        && request.ContextTokens == 4096
                                                                                                        && request.LaunchPolicy
                                                                                                        == LlamaServerBenchmarkLaunchPolicy.DeterministicV1),
            Arg.Any<Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task>>(),
            Arg.Any<Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<TransientLlamaServerEvaluationSession>>>(),
            Arg.Any<CancellationToken>());
        AssertEx.NotNull(client.LastOptions);
        _ = await store.Received(1).BindExecutionProvenanceAsync(evaluation.Id, Arg.Any<ReadOnlyMemory<byte>>(), CancellationToken.None);
    }

    [Test]
    public async Task Evaluation_BaseAndTunedUseEquivalentControlledHarnessPolicy_WithoutOrdinaryProvider()
    {
        var tunedPath = Path.Combine(_root, "tuned-equivalent.gguf");
        _ = Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(tunedPath, "tuned");
        var tunedSha = Convert.ToHexStringLower(SHA256.HashData("tuned"u8));
        var artifactId = Guid.NewGuid();
        var baseEvaluation = Evaluation();
        var tunedEvaluation = Evaluation() with
        {
            ModelName = "tuned-equivalent.gguf",
            ModelContentFingerprint = tunedSha,
            TargetKind = EvaluationModelTargetKind.StagedTrainingArtifact,
            SourceArtifactId = artifactId
        };
        var artifact = new TrainingArtifactRecord(artifactId, tunedEvaluation.TrainingRunId!.Value, TrainingArtifactKind.MergedGguf,
            tunedPath, tunedSha, 5, TrainingArtifactSmokeState.Passed, null, null, 2, 0, 0);
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var requests = new List<TransientLlamaServerEvaluationRequest>();
        var harness = EvaluationHarness(requests: requests);
        using var client = new RecordingChatClient();
        var baseStore = StoreFor(baseEvaluation);
        var tunedStore = StoreFor(tunedEvaluation);
        var baseExecutor = await CreateExecutorAsync(baseStore, datasets, client, baseEvaluation, evaluationHarness: harness);
        var tunedExecutor = await CreateExecutorAsync(tunedStore, datasets, client, tunedEvaluation, artifact: artifact, evaluationHarness: harness);

        await baseExecutor.ExecuteAsync(Claim(baseEvaluation.Id), CancellationToken.None);
        await tunedExecutor.ExecuteAsync(Claim(tunedEvaluation.Id), CancellationToken.None);

        AssertEx.Equal(expected: 2, requests.Count);
        AssertEx.True(requests.All(static request => request.ContextTokens == 4096));
        AssertEx.True(requests.All(static request => request.LaunchPolicy == LlamaServerBenchmarkLaunchPolicy.DeterministicV1));
        AssertEx.NotEqual(requests[0].ModelFilePath, requests[1].ModelFilePath);
    }

    [Test]
    public async Task Evaluation_InstalledBaseFingerprintReplacement_FailsBeforeHarnessLaunch()
    {
        var evaluation = Evaluation();
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var store = StoreFor(evaluation);
        var harness = EvaluationHarness();
        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store,
            datasets,
            client,
            evaluation,
            evaluationHarness: harness,
            installedFingerprint: "v1:replacement");

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        AssertEx.Empty(harness.ReceivedCalls());
        _ = await store.Received(1).CompleteAsync(evaluation.Id, TrainingWorkStatus.Failed,
            "The exact installed model identity recorded for this evaluation is no longer available.", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Evaluation_AdapterBaseFingerprintReplacement_FailsBeforeHarnessLaunch()
    {
        var path = Path.Combine(_root, "adapter-F16.gguf");
        _ = Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, "adapter");
        var sha = Convert.ToHexStringLower(SHA256.HashData("adapter"u8));
        var artifactId = Guid.NewGuid();
        var evaluation = Evaluation() with
        {
            ModelName = "adapter-F16.gguf",
            ModelContentFingerprint = sha,
            TargetKind = EvaluationModelTargetKind.StagedTrainingArtifact,
            SourceArtifactId = artifactId
        };
        var artifact = new TrainingArtifactRecord(artifactId, evaluation.TrainingRunId!.Value, TrainingArtifactKind.AdapterGguf,
            path, sha, 7, TrainingArtifactSmokeState.Passed, null, null, 2, 0, 0);
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var store = StoreFor(evaluation);
        var harness = EvaluationHarness();
        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store,
            datasets,
            client,
            evaluation,
            artifact: artifact,
            evaluationHarness: harness,
            installedFingerprint: "v1:replacement");

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        AssertEx.Empty(harness.ReceivedCalls());
        _ = await store.Received(1).CompleteAsync(evaluation.Id, TrainingWorkStatus.Failed,
            "The exact installed model identity recorded for this evaluation is no longer available.", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Evaluation_InstalledBaseBytesDoNotMatchCoordinatedSnapshot_FailsClosed()
    {
        var evaluation = Evaluation();
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var store = StoreFor(evaluation);
        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store,
            datasets,
            client,
            evaluation,
            installedModelSha256: new string('f', 64));

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        _ = await store.Received(1).CompleteAsync(evaluation.Id,
            TrainingWorkStatus.Failed,
            "The transient evaluation base model does not match the coordinated installed identity.",
            Arg.Any<CancellationToken>());
        _ = await store.DidNotReceiveWithAnyArgs()
                       .BindExecutionProvenanceAsync(Guid.Empty, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
    }

    [Test]
    [Arguments(true, true, "The transient evaluation runtime returned incomplete or mismatched launch provenance.")]
    [Arguments(false, false, "The transient evaluation runtime did not provide complete teardown evidence.")]
    public async Task Evaluation_InvalidHarnessEvidence_FailsClosed(bool mismatchedProvenance,
        bool completeTeardown,
        string expectedReason)
    {
        var evaluation = Evaluation();
        var datasets = Substitute.For<ITrainingDatasetStore>();
        _ = datasets.GetDatasetAsync(DatasetId, Arg.Any<CancellationToken>()).Returns(Dataset(Body("PINNED", "pinned_tool")));
        var store = StoreFor(evaluation);
        using var client = new RecordingChatClient();
        var executor = await CreateExecutorAsync(store,
            datasets,
            client,
            evaluation,
            evaluationHarness: EvaluationHarness(mismatchedProvenance: mismatchedProvenance, completeTeardown: completeTeardown));

        await executor.ExecuteAsync(Claim(evaluation.Id), CancellationToken.None);

        _ = await store.Received(1).CompleteAsync(evaluation.Id, TrainingWorkStatus.Failed, expectedReason, Arg.Any<CancellationToken>());
        _ = await store.Received(1)
                       .BindExecutionProvenanceAsync(evaluation.Id, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
    }

    private async Task<EvaluationRunExecutor> CreateExecutorAsync(ITrainingEvaluationStore store,
        ITrainingDatasetStore datasets,
        IChatClient client,
        TrainingEvaluationRecord evaluation,
        FrozenFixtureMode mode = FrozenFixtureMode.Current,
        ITrainingRunEventBuffer? events = null,
        TrainingArtifactRecord? artifact = null,
        ITransientLlamaServerEvaluationHarness? evaluationHarness = null,
        IInferenceChatClientFactory? chatClientFactory = null,
        Exception? harnessFailure = null,
        Exception? clientFactoryFailure = null,
        string? installedFingerprint = null,
        string? installedModelSha256 = null)
    {
        var workspace = new TrainingRunWorkspace(new FixedNodeDataDirectory(_root), _keyHolder);
        var membership = JsonSerializer.Deserialize<TrainingEvaluationMembershipV1>(evaluation.MembershipJson.Span, TrainingJson.Options)!;
        var freeze = new TrainingRunFreezeV1
        {
            FreezeId = membership.FreezeId,
            DatasetContentFingerprint = membership.DatasetContentFingerprint,
            HoldoutSampleIds = membership.HoldoutSampleIds,
            HoldoutSequences = [0]
        };
        var corpus = mode switch
        {
            FrozenFixtureMode.LegacyV1 => Encoding.UTF8.GetBytes(
                """{"sequence":0,"kind":"tool-call","label":"Good","reviewState":"Approved","parts":[{"kind":"user","sequence":0,"content":"call the tool"},{"kind":"tool","sequence":1,"toolName":"pinned_tool","arguments":"{}"}]}""" +
                "\n"),
            FrozenFixtureMode.MissingHoldout => FrozenTrainingCorpus.Write([Sample(Guid.NewGuid())]),
            _ => FrozenTrainingCorpus.Write([Sample()])
        };
        if (mode == FrozenFixtureMode.LegacyV1)
        {
            freeze = freeze with
            {
                SchemaVersion = 1
            };
        }

        freeze = freeze with
        {
            FrozenCopySha256 = Convert.ToHexStringLower(SHA256.HashData(corpus.Span))
        };
        if (mode == FrozenFixtureMode.Corrupt)
        {
            corpus = corpus.ToArray().Concat([(byte)' ']).ToArray();
        }

        if (mode == FrozenFixtureMode.FreezeMismatch)
        {
            freeze = freeze with
            {
                FreezeId = Guid.NewGuid()
            };
        }

        await workspace.WriteFrozenDatasetAsync(DatasetId, freeze.FreezeId, corpus, CancellationToken.None);
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.GetAsync(membership.TrainingRunId, Arg.Any<CancellationToken>()).Returns(Run(membership.TrainingRunId, freeze));
        if (artifact is not null)
        {
            _ = runs.GetArtifactAsync(artifact.Id, Arg.Any<CancellationToken>()).Returns(artifact);
        }

        _ = store.BindExecutionProvenanceAsync(evaluation.Id, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(evaluation);
        var modelPath = Path.Combine(_root, "base.gguf");
        await File.WriteAllTextAsync(modelPath, "base");
        var installedModels = Substitute.For<ITrainingEvaluationInstalledModelLeaseProvider>();
        var modelBytes = await File.ReadAllBytesAsync(modelPath);
        await using var installedLease = new FixedInstalledModelLease(modelPath,
            installedFingerprint ?? (artifact?.Kind == TrainingArtifactKind.AdapterGguf
                ? "v1:base"
                : evaluation.ModelContentFingerprint ?? string.Empty),
            installedModelSha256
            ?? Convert.ToHexStringLower(SHA256.HashData(modelBytes)),
            modelBytes.LongLength);
        _ = installedModels.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                           .Returns(installedLease);
        var factory = chatClientFactory ?? Substitute.For<IInferenceChatClientFactory>();
        if (clientFactoryFailure is not null)
        {
            _ = factory.CreateChatClient(Arg.Any<Uri>(), Arg.Any<string>()).Returns<IChatClient>(_ => throw clientFactoryFailure);
        }
        else if (chatClientFactory is null)
        {
            _ = factory.CreateChatClient(Arg.Any<Uri>(), Arg.Any<string>()).Returns(client);
        }

        return new EvaluationRunExecutor(store, runs, datasets, workspace,
            evaluationHarness ?? EvaluationHarness(harnessFailure),
            factory,
            installedModels,
            events ?? Substitute.For<ITrainingRunEventBuffer>(),
            new TrainingRunCancellationRegistry(), NullLogger<EvaluationRunExecutor>.Instance);
    }

    private static ITrainingEvaluationStore StoreFor(TrainingEvaluationRecord evaluation)
    {
        var store = Substitute.For<ITrainingEvaluationStore>();
        _ = store.GetAsync(evaluation.Id, Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.AppendResultsAsync(evaluation.Id, Arg.Any<IReadOnlyList<TrainingEvaluationResultEntry>>(), Arg.Any<CancellationToken>())
                 .Returns(evaluation);
        _ = store.BindExecutionProvenanceAsync(evaluation.Id, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(evaluation);
        _ = store.CompleteAsync(evaluation.Id, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(evaluation);
        return store;
    }

    private static TrainingRunRecord Run(Guid runId, TrainingRunFreezeV1 freeze) =>
        new(runId, DatasetId, FrozenFingerprint, 1, JsonSerializer.SerializeToUtf8Bytes(freeze, TrainingJson.Options), Guid.NewGuid(), "tuned-model",
            "v1:base",
            ReadOnlyMemory<byte>.Empty, null, TrainingRunStatus.Succeeded, null, null, null, null, 1, 0, 0, TrainingWorkStatus.Succeeded, null);

    private static DatasetDefinitionBodyV1 Body(string instructions, string toolName) =>
        new()
        {
            TeacherModelName = "teacher.gguf",
            SystemInstructions = instructions,
            Tools = [new DatasetToolSnapshotV1(toolName, "does a thing", """{"type":"object"}""", RequiresApproval: false, ToolCategory.ReadLocal)],
            SampleKinds = [new DatasetSampleKindTargetV1("tool-call", Count: 1, TrainingSampleLabel.Good)]
        };

    /// <summary>A null body is a dataset created before pinning existed — the only way the column reads as absent.</summary>
    private static TrainingDatasetRecord Dataset(DatasetDefinitionBodyV1? pinnedBody, string? contentFingerprint = null)
    {
        ReadOnlyMemory<byte>? definitionJson = pinnedBody is null
            ? null
            : new ReadOnlyMemory<byte>(JsonSerializer.SerializeToUtf8Bytes(pinnedBody, TrainingJson.Options));
        return new TrainingDatasetRecord(DatasetId, Guid.NewGuid(), 1, definitionJson, "dataset", TrainingDatasetStatus.Ready, 1,
            contentFingerprint ?? FrozenFingerprint, 1, 1, 0, 0, 0, 1, 0, 0, DatasetGenerationWorkStatus.Succeeded, null);
    }

    private static TrainingDefinitionRecord DefinitionRecord(DatasetDefinitionBodyV1 body) =>
        new(Guid.NewGuid(), "definition", TrainingDatasetKind.ToolCalling, JsonSerializer.SerializeToUtf8Bytes(body, TrainingJson.Options),
            DefinitionVersion: 2, Version: 2, CreatedAtUtc: 0, UpdatedAtUtc: 0);

    private static TrainingSampleRecord Sample(Guid? sampleId = null)
    {
        var content = new TrainingSampleContentV1
        {
            Parts =
            [
                new TrainingSamplePartV1("user", 0, "call the tool"),
                new TrainingSamplePartV1("tool", 1, ToolName: "pinned_tool", Arguments: "{}")
            ]
        };
        return new TrainingSampleRecord(sampleId ?? SampleId, DatasetId, 0, "tool-call", TrainingSampleLabel.Good, TrainingSampleReviewState.Approved,
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
            DatasetContentFingerprint = FrozenFingerprint,
            HoldoutSampleIds = [SampleId]
        };
        return new TrainingEvaluationRecord(Guid.NewGuid(),
            membership.TrainingRunId,
            ComparisonId: null,
            "tuned-model",
            ModelContentFingerprint: "v1:base",
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

    private static ITransientLlamaServerEvaluationHarness EvaluationHarness(Exception? failure = null,
        ICollection<TransientLlamaServerEvaluationRequest>? requests = null,
        bool mismatchedProvenance = false,
        bool completeTeardown = true)
    {
        var harness = Substitute.For<ITransientLlamaServerEvaluationHarness>();
        _ = harness.RunAsync(Arg.Any<TransientLlamaServerEvaluationRequest>(),
                       Arg.Any<Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task>>(),
                       Arg.Any<Func<TransientLlamaServerEvaluationSession, CancellationToken, Task<TransientLlamaServerEvaluationSession>>>(),
                       Arg.Any<CancellationToken>())
                   .Returns(async call =>
                   {
                       if (failure is not null)
                       {
                           throw failure;
                       }

                       var request = call.ArgAt<TransientLlamaServerEvaluationRequest>(0);
                       requests?.Add(request);
                       var model = await FileIdentityAsync(request.ModelFilePath, request.AdapterFilePath);
                       var launch = LaunchReceipt();
                       var binder = call.ArgAt<Func<TransientLlamaServerEvaluationProvenance, CancellationToken, Task>>(1);
                       await binder(new TransientLlamaServerEvaluationProvenance(model, launch), CancellationToken.None);
                       var session = new TransientLlamaServerEvaluationSession(new Uri("http://127.0.0.1:18080/v1"), model.ModelId, model, launch);
                       var body = call.ArgAt<Func<TransientLlamaServerEvaluationSession, CancellationToken,
                           Task<TransientLlamaServerEvaluationSession>>>(2);
                       var value = await body(session, CancellationToken.None);
                       var returnedLaunch = mismatchedProvenance
                           ? launch with
                           {
                               ExecutableVersion = "different"
                           }
                           : launch;
                       return new TransientLlamaServerEvaluationResult<TransientLlamaServerEvaluationSession>(value,
                           model,
                           returnedLaunch,
                           new TransientLlamaServerTeardownEvidence(42,
                               TreeKillRequested: true,
                               ProcessExitObserved: completeTeardown,
                               ExitObservationTimedOut: !completeTeardown,
                               HandleDisposed: true));
                   });
        return harness;
    }

    private static async Task<TransientLlamaServerModelProvenance> FileIdentityAsync(string modelPath, string? adapterPath)
    {
        var modelBytes = await File.ReadAllBytesAsync(modelPath);
        var adapterBytes = adapterPath is null ? null : await File.ReadAllBytesAsync(adapterPath);
        return new TransientLlamaServerModelProvenance(Path.GetFileName(modelPath),
            modelBytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(modelBytes)),
            adapterPath is null ? null : Path.GetFileName(adapterPath),
            adapterBytes?.LongLength,
            adapterBytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(adapterBytes)));
    }

    private static LlamaServerLaunchReceipt LaunchReceipt()
    {
        var projection = new LlamaServerLaunchProjection(AutoFit: false,
            Metrics: true,
            ContextTokens: 4096,
            GpuLayers: null,
            TensorSplit: null,
            OverrideTensor: null,
            CpuMoe: false,
            KvCacheTypeK: null,
            KvCacheTypeV: null,
            LlamaServerLaunchProjection.FlashAttentionAuto,
            Threads: 4,
            ThreadsBatch: 4,
            BatchSize: 512,
            UbatchSize: 512,
            Parallel: 1,
            CacheReuse: null,
            CacheRamMiB: 0,
            Jinja: true,
            Pooling: null);
        var executableSha256 = new string('e', 64);
        return new LlamaServerLaunchReceipt(LlamaServerLaunchReceipt.CurrentVersion,
            GpuVariant.Cuda,
            "linux",
            "v1",
            executableSha256,
            executableSha256,
            projection,
            new LlamaServerLaunchAuxAssets(HasLora: false, HasMmproj: false, HasDraft: false),
            new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Unknown, null, null),
            EffectiveContextTokens: 4096,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);
    }

    public enum FrozenFixtureMode
    {
        Current,
        Corrupt,
        FreezeMismatch,
        MissingHoldout,
        LegacyV1
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

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(exception);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

        public void Dispose() { }
    }

    private sealed class FixedInstalledModelLease(
        string modelFilePath,
        string modelContentFingerprint,
        string modelSha256,
        long modelSizeBytes) : ITrainingEvaluationInstalledModelLease
    {
        public string ModelFilePath { get; } = modelFilePath;
        public string ModelContentFingerprint { get; } = modelContentFingerprint;
        public string ModelSha256 { get; } = modelSha256;
        public long ModelSizeBytes { get; } = modelSizeBytes;

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
