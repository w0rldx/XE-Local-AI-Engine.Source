namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.E2ETests.Common;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Tracked, opt-in lifecycle proof over the real E2E host, authenticated HTTP routes, encrypted SQLite stores,
///     immutable freeze writer, comparison policy and quality policy. External Python/llama.cpp execution and the
///     final registry move are the only deterministic doubles; every required phase records a verdict and the test
///     refuses to pass if even one is absent.
/// </summary>
public sealed class TrainingLifecycleE2ETests : XESerialE2ETestBase
{
    private const string Api = "/api/local/v1/training";

    [Test]
    public async Task TrainingLifecycle_FreezeThroughExplicitPromotion_RecordsEveryRequiredVerdict()
    {
        var verdicts = Factory.Services.GetRequiredService<TrainingLifecycleE2ETestDoubles.Verdicts>();
        verdicts.Reset();
        var fixture = await SeedDatasetAndCheckpointAsync().ConfigureAwait(false);
        var token = await LoginForApiAsync().ConfigureAwait(false);

        using var runDocument = await SendJsonAsync(HttpMethod.Post,
                $"{Api}/runs",
                token,
                new
                {
                    datasetId = fixture.DatasetId,
                    expectedDatasetVersion = fixture.DatasetVersion,
                    baseArtifactId = fixture.BaseArtifactId,
                    licenseConfirmed = true,
                    linkedModelName = TrainingLifecycleE2ETestDoubles.InstalledBaseModel
                },
                expectedStatus: 200)
            .ConfigureAwait(false);
        var runId = runDocument.RootElement.GetProperty("id").GetGuid();

        TrainingRunRecord run;
        TrainingRunFreezeV1 freeze;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var runs = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
            run = Check.NotNull(await runs.GetAsync(runId).ConfigureAwait(false), "The run endpoint persisted the run.");
            freeze = Check.NotNull(JsonSerializer.Deserialize<TrainingRunFreezeV1>(run.FreezeJson.Span, TrainingJson.Options),
                "The run carries a readable immutable freeze.");
            Check.Equal(TrainingRunFreezeV1.CurrentSchemaVersion, freeze.SchemaVersion);
            Check.True(freeze.HoldoutSampleIds.Count > 0, "The lifecycle must evaluate a real non-empty hold-out membership.");
            verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.RunFrozen);

            var claim = Check.NotNull(await runs.ClaimNextAsync(TrainingWorkKind.TrainingRun).ConfigureAwait(false),
                "The run endpoint must enqueue real durable work.");
            await scope.ServiceProvider.GetRequiredService<ITrainingRunExecutor>()
                       .ExecuteAsync(claim, CancellationToken.None)
                       .ConfigureAwait(false);
            run = Check.NotNull(await runs.GetAsync(runId).ConfigureAwait(false), "The production executor retained the run.");
            Check.Equal(TrainingRunStatus.Succeeded, run.Status);
            Check.True((await runs.ListArtifactsAsync(runId).ConfigureAwait(false)).Any(item => item.Kind == TrainingArtifactKind.HfAdapterDir),
                "The production executor must register the trainer's staged adapter output.");
            verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.TrainingSucceeded);
        }

        using var exportDocument = await SendJsonAsync(HttpMethod.Post,
                $"{Api}/runs/{runId}/exports",
                token,
                new
                {
                    kind = "MergedGguf",
                    quantType = "Q4_K_M"
                },
                expectedStatus: 202)
            .ConfigureAwait(false);
        Check.Equal("MergedGguf", exportDocument.RootElement.GetProperty("kind").GetString());

        var artifact = await WaitForExportAsync(runId).ConfigureAwait(false);
        var artifactId = artifact.Id;
        verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.ExportStaged);
        verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.SmokePassed);

        var (baseEvaluationId, tunedEvaluationId) = await CompleteEvaluationsAsync(token, runId, artifact, verdicts).ConfigureAwait(false);

        using var comparisonDocument = await SendJsonAsync(HttpMethod.Post,
                $"{Api}/comparisons",
                token,
                new
                {
                    name = $"E2E lifecycle {runId:N}",
                    baseEvaluationRunId = baseEvaluationId,
                    tunedEvaluationRunId = tunedEvaluationId,
                    trainingRunId = runId
                },
                expectedStatus: 200)
            .ConfigureAwait(false);
        var comparisonId = comparisonDocument.RootElement.GetProperty("id").GetGuid();
        Check.True(comparisonDocument.RootElement.GetProperty("deltas").GetProperty("accuracyAvailable").GetBoolean(),
            "A comparison with no scored work is not a lifecycle verdict.");
        verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.ComparisonCreated);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            artifact = Check.NotNull(await scope.ServiceProvider.GetRequiredService<ITrainingRunStore>()
                                                .GetArtifactAsync(artifactId)
                                                .ConfigureAwait(false), "The staged artifact still exists before quality review.");
        }

        using var qualityDocument = await SendJsonAsync(HttpMethod.Put,
                $"{Api}/artifacts/{artifactId}/quality",
                token,
                new
                {
                    comparisonId,
                    expectedVersion = artifact.Version
                },
                expectedStatus: 200)
            .ConfigureAwait(false);
        Check.Equal("Passed", qualityDocument.RootElement.GetProperty("outcome").GetString());
        verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.QualityPassed);

        using var promotionDocument = await SendJsonAsync(HttpMethod.Post,
                $"{Api}/artifacts/{artifactId}/promote",
                token,
                new
                {
                    modelName = $"e2e-trained-{runId:N}"
                },
                expectedStatus: 200)
            .ConfigureAwait(false);
        Check.Contains(promotionDocument.RootElement.GetProperty("modelName").GetString()!, ":Q4_K_M", StringComparison.Ordinal);

        await Page.GotoAsync($"{NodeAppUrl}/training/comparisons", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        }).ConfigureAwait(false);
        await Expect(Page.GetByText($"E2E lifecycle {runId:N}")).ToBeVisibleAsync().ConfigureAwait(false);

        verdicts.AssertComplete();
        Check.Equal(Enum.GetValues<TrainingLifecycleE2ETestDoubles.Stage>().Length, verdicts.Snapshot().Count,
            "Duplicate calls do not compensate for a skipped lifecycle phase.");
    }

    private async Task<SeedFixture> SeedDatasetAndCheckpointAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var datasets = scope.ServiceProvider.GetRequiredService<ITrainingDatasetStore>();
        var definition = await datasets.CreateDefinitionAsync(new TrainingDefinitionInput($"E2E lifecycle {Guid.NewGuid():N}",
            TrainingDatasetKind.ToolCalling,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"holdoutFraction":0.2}"""))).ConfigureAwait(false);
        var dataset = await datasets.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id,
            definition.Version,
            $"E2E lifecycle {Guid.NewGuid():N}")).ConfigureAwait(false);
        _ = Check.NotNull(await datasets.ClaimNextAsync().ConfigureAwait(false), "Dataset generation must own a durable work item.");
        for (var index = 0; index < 10; index++)
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(new TrainingSampleContentV1
            {
                Parts = [new TrainingSamplePartV1("user", 0, Content: $"question-{index}")]
            }, TrainingJson.Options);
            _ = await datasets.AppendSampleAsync(new TrainingSampleInput(dataset.Id,
                                  "no-tool",
                                  TrainingSampleLabel.Good,
                                  content,
                                  ValidationJson: null,
                                  TrainingSampleProvenance.Generated,
                                  new string((char)('a' + index), count: 64)))
                              .ConfigureAwait(false);
        }

        var ready = await datasets.CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Succeeded, errorMessage: null).ConfigureAwait(false);
        var baseArtifacts = scope.ServiceProvider.GetRequiredService<ITrainingBaseArtifactStore>();
        var downloading = await baseArtifacts.StartDownloadAsync("e2e/base", new string('b', 40)).ConfigureAwait(false);
        var checkpoint = await baseArtifacts.MarkReadyAsync(downloading.Id,
            downloading.Version,
            Encoding.UTF8.GetBytes("[]"),
            totalBytes: 1,
            licenseJson: null).ConfigureAwait(false);
        return new SeedFixture(ready.Id, ready.Version, checkpoint.Id);
    }

    private async Task<(Guid Base, Guid Tuned)> CompleteEvaluationsAsync(string token,
        Guid runId,
        TrainingArtifactRecord artifact,
        TrainingLifecycleE2ETestDoubles.Verdicts verdicts)
    {
        using var baseDocument = await SendJsonAsync(HttpMethod.Post, $"{Api}/evaluations", token,
                new
                {
                    trainingRunId = runId,
                    target = "Base",
                    modelName = TrainingLifecycleE2ETestDoubles.InstalledBaseModel
                }, 202)
            .ConfigureAwait(false);
        var baseId = baseDocument.RootElement.GetProperty("id").GetGuid();
        await ExecuteEvaluationAsync(baseId).ConfigureAwait(false);
        verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.BaseEvaluationSucceeded);

        using var tunedDocument = await SendJsonAsync(HttpMethod.Post, $"{Api}/evaluations", token,
            new
            {
                trainingRunId = runId,
                target = "Tuned",
                artifactId = artifact.Id
            }, 202).ConfigureAwait(false);
        var tunedId = tunedDocument.RootElement.GetProperty("id").GetGuid();
        await ExecuteEvaluationAsync(tunedId).ConfigureAwait(false);
        verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.TunedEvaluationSucceeded);
        return (baseId, tunedId);
    }

    private async Task ExecuteEvaluationAsync(Guid expectedEvaluationId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        var evaluations = scope.ServiceProvider.GetRequiredService<ITrainingEvaluationStore>();
        var claim = Check.NotNull(await runs.ClaimNextAsync(TrainingWorkKind.EvaluationRun).ConfigureAwait(false),
            "Every evaluation must be claimed from the shared durable queue.");
        Check.Equal(expectedEvaluationId, claim.TargetId);
        await scope.ServiceProvider.GetRequiredService<IEvaluationRunExecutor>()
                   .ExecuteAsync(claim, CancellationToken.None)
                   .ConfigureAwait(false);
        var completed = Check.NotNull(await evaluations.GetAsync(expectedEvaluationId).ConfigureAwait(false),
            "The production evaluation executor retained its verdict.");
        Check.Equal(TrainingEvaluationStatus.Succeeded, completed.Status);
        Check.Equal(completed.TotalCount, completed.ScoredCount, "An incomplete evaluation must never count as a lifecycle verdict.");
        Check.True(completed.ExecutionProvenanceJson is { IsEmpty: false }, "Evaluation must bind validated launch provenance before scoring.");
        var provenance = Check.NotNull(JsonSerializer.Deserialize<TrainingEvaluationExecutionProvenanceV1>(completed.ExecutionProvenanceJson!.Value.Span, TrainingJson.Options),
            "The executor must persist readable launch provenance.");
        Check.Equal(provenance.ExecutableSha256, provenance.ManifestSha256,
            "The runtime executable and manifest identities must be the same verified binary identity.");
    }

    private async Task<TrainingArtifactRecord> WaitForExportAsync(Guid runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await using var scope = Factory.Services.CreateAsyncScope();
            var artifacts = await scope.ServiceProvider.GetRequiredService<ITrainingRunStore>()
                                       .ListArtifactsAsync(runId, timeout.Token)
                                       .ConfigureAwait(false);
            var artifact = artifacts.SingleOrDefault(item => item.Kind == TrainingArtifactKind.MergedGguf);
            if (artifact?.SmokeState == TrainingArtifactSmokeState.Passed)
            {
                Check.True(File.Exists(artifact.Path), "A staged export verdict without staged bytes is not an export.");
                return artifact;
            }

            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<string> LoginForApiAsync()
    {
        var response = await Context.APIRequest.PostAsync($"{NodeAppUrl}/api/local/v1/auth/login", new APIRequestContextOptions
        {
            DataObject = new
            {
                email = XENodeE2EWebApplicationFactory.AdminEmail,
                password = XENodeE2EWebApplicationFactory.AdminPassword
            }
        }).ConfigureAwait(false);
        Check.True(response.Ok, $"API login failed with HTTP {response.Status} {response.StatusText}.");
        using var document = JsonDocument.Parse(await response.TextAsync().ConfigureAwait(false));
        return document.RootElement.GetProperty("accessToken").GetString()
               ?? throw new InvalidOperationException("API login returned no access token.");
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method,
        string path,
        string token,
        object body,
        int expectedStatus)
    {
        var options = new APIRequestContextOptions
        {
            DataObject = body,
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + token,
                ["Origin"] = NodeAppUrl
            }
        };
        var response = method == HttpMethod.Post
            ? await Context.APIRequest.PostAsync(NodeAppUrl + path, options).ConfigureAwait(false)
            : await Context.APIRequest.PutAsync(NodeAppUrl + path, options).ConfigureAwait(false);
        var text = await response.TextAsync().ConfigureAwait(false);
        Check.Equal(expectedStatus, response.Status, $"{method} {path} returned {response.Status}: {text}");
        return JsonDocument.Parse(text);
    }

    private sealed record SeedFixture(Guid DatasetId, long DatasetVersion, Guid BaseArtifactId);
}

/// <summary>The negative control: without every named verdict the lifecycle guard must fail rather than report green.</summary>
public sealed class TrainingLifecycleVerdictTests
{
    [Test]
    public void LifecycleVerifier_WhenAStageIsSkipped_RefusesToPass()
    {
        var verdicts = new TrainingLifecycleE2ETestDoubles.Verdicts();
        verdicts.Record(TrainingLifecycleE2ETestDoubles.Stage.RunFrozen);

        try
        {
            verdicts.AssertComplete();
            throw new InvalidOperationException("The negative control unexpectedly accepted a skipped lifecycle.");
        }
        catch (InvalidOperationException exception)
        {
            Check.Contains(exception.Message, nameof(TrainingLifecycleE2ETestDoubles.Stage.Promoted), StringComparison.Ordinal);
        }
    }
}

internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message ?? $"Expected '{expected}', got '{actual}'.");
        }
    }

    public static T NotNull<T>(T? value, string message) where T : class =>
        value ?? throw new InvalidOperationException(message);

    public static void Contains(string value, string expected, StringComparison comparison)
    {
        if (!value.Contains(expected, comparison))
        {
            throw new InvalidOperationException($"Expected '{value}' to contain '{expected}'.");
        }
    }
}
