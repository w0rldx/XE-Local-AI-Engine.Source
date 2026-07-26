namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Capability probing and argument projection coverage for the machine-readable fit helper.</summary>
public sealed class LlamaFitParamsProcessRunnerTests
{
    [Test]
    public async Task RunAsync_WhenSiblingHelperIsMissing_ReportsMissingCapability()
    {
        using var temp = new TempDirectory();
        var spec = CreateSpec(temp.Path);
        var runner = new LlamaFitParamsProcessRunner();

        var result = await runner.RunAsync(spec, CancellationToken.None);

        AssertEx.Equal(LlamaFitParamsRunStatus.MissingCapability, result.Status);
        AssertEx.Equal(expected: 0, result.StandardOutput.Count);
        AssertEx.Null(result.FailureReason);
    }

    [Test]
    public void BuildArguments_ProjectsOnlyFitRelevantCommonOptions()
    {
        var spec = CreateSpec("/runtime");

        var arguments = LlamaFitParamsProcessRunner.BuildArguments(spec.Arguments);

        AssertEx.SequenceEqual(
        [
            "-m", "/models/model.gguf",
            "--parallel", "1",
            "--fit", "on",
            "-c", "8192",
            "-fa", "on",
            "-ctk", "q8_0",
            "-ctv", "q8_0",
            "--pooling", "rank"
        ], arguments);
        AssertEx.False(arguments.Contains("--host"));
        AssertEx.False(arguments.Contains("--port"));
        AssertEx.False(arguments.Contains("--metrics"));
        AssertEx.False(arguments.Contains("--no-warmup"));
        AssertEx.False(arguments.Contains("--rerank"));
    }

    private static LlamaServerLaunchSpec CreateSpec(string workingDirectory) =>
        new("model",
            ModelRole.Reranker,
            Path.Combine(workingDirectory, "llama-server"),
            [
                "-m", "/models/model.gguf",
                "--host", "127.0.0.1",
                "--port", "18080",
                "--parallel", "1",
                "--no-warmup",
                "--fit", "on",
                "--metrics",
                "-c", "8192",
                "-fa", "on",
                "-ctk", "q8_0",
                "-ctv", "q8_0",
                "--rerank",
                "--pooling", "rank"
            ],
            Port: 18080,
            workingDirectory);
}
