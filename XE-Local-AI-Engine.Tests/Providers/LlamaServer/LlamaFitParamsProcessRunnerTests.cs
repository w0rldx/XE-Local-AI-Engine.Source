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
        using var temp = new TestDirectory();
        var spec = CreateSpec(temp.Path);
        var runner = new LlamaFitParamsProcessRunner();

        var result = await runner.RunAsync(spec, CancellationToken.None);

        AssertEx.Equal(LlamaFitParamsRunStatus.MissingCapability, result.Status);
        AssertEx.Equal(expected: 0, result.StandardOutput.Count);
        AssertEx.Null(result.FailureReason);
    }

    [Test]
    public async Task RunAsync_WhenHelperFails_ReturnsBoundedSanitizedStandardErrorExcerpt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TestDirectory();
        var helper = Path.Combine(temp.Path, "llama-fit-params");
        await File.WriteAllTextAsync(helper,
            "#!/bin/sh\nprintf '%s' 'fatal detail at /home/sam/private/model.gguf token=super-secret-value' >&2\nexit 17\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runner = new LlamaFitParamsProcessRunner();

        var result = await runner.RunAsync(CreateSpec(temp.Path), CancellationToken.None);

        AssertEx.Equal(LlamaFitParamsRunStatus.Failed, result.Status);
        AssertEx.Contains(result.FailureReason!, "fatal detail");
        AssertEx.False(result.FailureReason!.Contains("/home/sam", StringComparison.Ordinal));
        AssertEx.False(result.FailureReason.Contains("super-secret-value", StringComparison.Ordinal));
        AssertEx.True(result.FailureReason.Length <= 320);
    }

    [Test]
    public void BuildArguments_ProjectsOnlyFitRelevantCommonOptions()
    {
        var spec = CreateSpec("/runtime");

        var arguments = LlamaFitParamsProcessRunner.BuildArguments(spec.Arguments);

        AssertEx.True(arguments.SequenceEqual([
            "-m", "/models/model.gguf",
            "--parallel", "1",
            "--fit", "on",
            "-c", "8192",
            "-fa", "on",
            "-ctk", "q8_0",
            "-ctv", "q8_0"
        ]));
        AssertEx.False(arguments.Contains("--host"));
        AssertEx.False(arguments.Contains("--port"));
        AssertEx.False(arguments.Contains("--metrics"));
        AssertEx.False(arguments.Contains("--no-warmup"));
        AssertEx.False(arguments.Contains("--rerank"));
        AssertEx.False(arguments.Contains("--pooling"),
            "Pinned b9692 llama-fit-params accepts fit-common options only; pooling stays on the server launch vector.");
        AssertEx.Contains(spec.Arguments, "--pooling");
    }

    [Test]
    public void BuildArguments_CarriesExpertPlacementToTheHelper()
    {
        // Without these the helper fits under a placement the server is NOT running (experts on the GPU), and its
        // stdout carries no -ot, so the frozen replay would silently drop the flag that made the placement true.
        var valueless = LlamaFitParamsProcessRunner.BuildArguments([
            "-m", "/models/moe.gguf", "--fit", "on", "--cpu-moe", "--metrics"
        ]);

        AssertEx.True(valueless.SequenceEqual(["-m", "/models/moe.gguf", "--fit", "on", "--cpu-moe"]));

        var shortForm = LlamaFitParamsProcessRunner.BuildArguments(["-m", "/models/moe.gguf", "-cmoe"]);

        AssertEx.True(shortForm.SequenceEqual(["-m", "/models/moe.gguf", "-cmoe"]));

        var counted = LlamaFitParamsProcessRunner.BuildArguments(["-m", "/models/moe.gguf", "--n-cpu-moe", "12"]);

        AssertEx.True(counted.SequenceEqual(["-m", "/models/moe.gguf", "--n-cpu-moe", "12"]));
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

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xe-fit-params-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
