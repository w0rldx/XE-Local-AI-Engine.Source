namespace XE_Local_AI_Engine.Tests.HostAgent;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Linux.Capabilities;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentCapabilityDetectorTests
{
    [Test]
    public async Task GetCapabilitiesAsync_WhenNvidiaCommandsSucceed_ReportsGpuSignalsSeparately()
    {
        using var tempDirectory = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(tempDirectory.Path, "usage.bin"), "12345");
        var processRunner = new FakeProcessRunner
        {
            Results = new Dictionary<string, ProcessResult>
            {
                ["nvidia-smi"] = Success(),
                ["nvidia-container-runtime"] = Success()
            }
        };
        var detector = new CapabilityDetector(processRunner,
            Options.Create(new HostAgentCapabilityOptions
            {
                RuntimeDataPath = tempDirectory.Path
            }),
            TimeProvider.System);

        var capabilities = await detector.GetCapabilitiesAsync();

        AssertEx.True(capabilities.CpuAvailable);
        AssertEx.True(capabilities.NvidiaGpuInference);
        AssertEx.True(capabilities.GpuRuntimeConfigured);
        AssertEx.Equal("unsupported", capabilities.AmdGpuStatus);
        AssertEx.Equal(5L, capabilities.RuntimeDiskBytes);
    }

    [Test]
    public async Task GetCapabilitiesAsync_WhenNvidiaRuntimeIsMissing_KeepsRuntimeConfiguredSeparate()
    {
        var processRunner = new FakeProcessRunner
        {
            Results = new Dictionary<string, ProcessResult>
            {
                ["nvidia-smi"] = Success(),
                ["nvidia-container-runtime"] = Failure()
            }
        };
        var detector = new CapabilityDetector(processRunner,
            Options.Create(new HostAgentCapabilityOptions
            {
                RuntimeDataPath = "/does/not/exist"
            }),
            TimeProvider.System);

        var capabilities = await detector.GetCapabilitiesAsync();

        AssertEx.True(capabilities.NvidiaGpuInference);
        AssertEx.False(capabilities.GpuRuntimeConfigured);
        AssertEx.Equal(0L, capabilities.RuntimeDiskBytes);
    }

    private static ProcessResult Success()
    {
        return new ProcessResult
        {
            ExitCode = 0,
            StandardOutput = string.Empty,
            StandardError = string.Empty
        };
    }

    private static ProcessResult Failure()
    {
        return new ProcessResult
        {
            ExitCode = 1,
            StandardOutput = string.Empty,
            StandardError = string.Empty
        };
    }

    private static TempDirectory CreateTempDirectory()
    {
        return new TempDirectory(Path.Combine(Path.GetTempPath(), $"xe-host-agent-{Guid.NewGuid():N}"));
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public IReadOnlyDictionary<string, ProcessResult> Results { get; init; } = new Dictionary<string, ProcessResult>();

        public Task<ProcessResult> RunAsync(string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Results[fileName]);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
