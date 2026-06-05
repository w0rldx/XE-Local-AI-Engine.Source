namespace XE_Local_AI_Engine.Tests.ModelFit;

using Google.Protobuf.WellKnownTypes;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fake;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Model-fit runner tests: the production-resident fake round-trips a scripted result and records the
///     intent request, and the gRPC runner's request→proto and reply→result mapping is asserted as a pure unit test
///     (no live channel).
/// </summary>
public sealed class ModelFitUtilityRunnerTests
{
    private const string ValidReference =
        "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

    private static ModelFitUtilityRunRequest RecommendRequest()
    {
        return new ModelFitUtilityRunRequest(
            ImageReference: ValidReference,
            Operation: ModelFitOperation.Recommend,
            UseCase: "coding",
            Limit: 5,
            ModelName: null,
            ProviderName: "ollama",
            ProviderUrl: null,
            AttachRuntimeNetwork: false,
            CpuCoresOverride: 4,
            RamOverrideGb: 16,
            VramOverrideGb: null,
            TimeoutSeconds: 120);
    }

    [Test]
    public async Task FakeRunner_RecordsRequestAndReturnsScriptedResult()
    {
        var runner = new FakeModelFitUtilityRunner();
        runner.ScriptResult(new ModelFitUtilityRunResult(
            Status: ModelFitRunStatus.Succeeded,
            ExitCode: 0,
            StandardOutput: """{"models":[]}""",
            StandardError: string.Empty,
            Completed: true,
            DurationMs: 42,
            StartedAtUtc: 100,
            CompletedAtUtc: 142,
            SanitizedError: null));

        var result = await runner.RunAsync(RecommendRequest());

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal("""{"models":[]}""", result.StandardOutput);
        AssertEx.Equal(42L, result.DurationMs);
        AssertEx.Equal(1, runner.RunCount);
        AssertEx.NotNull(runner.LastRequest);
        AssertEx.Equal(ValidReference, runner.LastRequest!.ImageReference);
        AssertEx.Equal("coding", runner.LastRequest.UseCase!);
    }

    [Test]
    public void GrpcRunner_ToMessage_MapsIntentRequestToProto()
    {
        var message = GrpcModelFitUtilityRunner.ToMessage(RecommendRequest());

        AssertEx.Equal(ValidReference, message.ImageReference);
        AssertEx.Equal(ModelFitOperationMessage.ModelFitOperationRecommend, message.Operation);
        AssertEx.Equal("coding", message.UseCase);
        AssertEx.Equal(5, message.Limit);
        AssertEx.Equal("ollama", message.ProviderName);
        AssertEx.Equal(ModelFitNetworkModeMessage.ModelFitNetworkModeNone, message.Network);
        AssertEx.Equal(4, message.CpuCoresOverride);
        AssertEx.Equal(16, message.RamOverrideGb);
        // Unset overrides map to 0 on the wire; an unset timeout maps to 0 (HostAgent uses its default).
        AssertEx.Equal(0, message.VramOverrideGb);
        AssertEx.Equal(120, message.TimeoutSeconds);
    }

    [Test]
    public void GrpcRunner_ToMessage_BenchmarkRequestAttachesRuntimeNetwork()
    {
        var message = GrpcModelFitUtilityRunner.ToMessage(new ModelFitUtilityRunRequest(
            ImageReference: ValidReference,
            Operation: ModelFitOperation.Benchmark,
            UseCase: null,
            Limit: 0,
            ModelName: "llama3",
            ProviderName: "ollama",
            ProviderUrl: "http://ollama:11434",
            AttachRuntimeNetwork: true,
            CpuCoresOverride: null,
            RamOverrideGb: null,
            VramOverrideGb: null,
            TimeoutSeconds: null));

        AssertEx.Equal(ModelFitOperationMessage.ModelFitOperationBenchmark, message.Operation);
        AssertEx.Equal("llama3", message.ModelName);
        AssertEx.Equal("http://ollama:11434", message.ProviderUrl);
        AssertEx.Equal(ModelFitNetworkModeMessage.ModelFitNetworkModeRuntime, message.Network);
        AssertEx.Equal(string.Empty, message.UseCase);
    }

    [Test]
    public void GrpcRunner_ToResult_MapsReplyToResult()
    {
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var completedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_005_000);
        var reply = new RunModelFitUtilityReply
        {
            Status = ModelFitTerminalStatusMessage.Succeeded,
            ExitCode = 0,
            StandardOutput = """{"models":[]}""",
            StandardError = string.Empty,
            Completed = true,
            DurationMs = 5000,
            StartedAt = Timestamp.FromDateTimeOffset(startedAt),
            CompletedAt = Timestamp.FromDateTimeOffset(completedAt),
            SanitizedError = string.Empty
        };

        var result = GrpcModelFitUtilityRunner.ToResult(reply);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(0, result.ExitCode);
        AssertEx.True(result.Completed);
        AssertEx.Equal(5000L, result.DurationMs);
        AssertEx.Equal(1_700_000_000_000L, result.StartedAtUtc ?? -1L);
        AssertEx.Equal(1_700_000_005_000L, result.CompletedAtUtc ?? -1L);
        // An empty sanitized error maps to null (no operator-facing error on success).
        AssertEx.Null(result.SanitizedError);
    }

    [Test]
    [Arguments(ModelFitTerminalStatusMessage.Failed, ModelFitRunStatus.Failed)]
    [Arguments(ModelFitTerminalStatusMessage.Cancelled, ModelFitRunStatus.Cancelled)]
    [Arguments(ModelFitTerminalStatusMessage.TimedOut, ModelFitRunStatus.TimedOut)]
    [Arguments(ModelFitTerminalStatusMessage.ModelFitTerminalStatusUnspecified, ModelFitRunStatus.Failed)]
    public void GrpcRunner_ToResult_MapsTerminalStatus(ModelFitTerminalStatusMessage wire, ModelFitRunStatus expected)
    {
        var result = GrpcModelFitUtilityRunner.ToResult(new RunModelFitUtilityReply
        {
            Status = wire,
            ExitCode = 1,
            Completed = false,
            SanitizedError = "model-fit utility run failed (exit code 1)"
        });

        AssertEx.Equal(expected, result.Status);
        AssertEx.NotNull(result.SanitizedError);
    }
}
