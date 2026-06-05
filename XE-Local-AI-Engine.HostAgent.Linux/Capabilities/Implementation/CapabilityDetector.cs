namespace XE_Local_AI_Engine.HostAgent.Linux.Capabilities.Implementation;

using System.ComponentModel;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

public sealed class CapabilityDetector
{
    private const string AmdUnsupported = "unsupported";
    private readonly IOptions<HostAgentCapabilityOptions> _options;
    private readonly IProcessRunner _processRunner;
    private readonly TimeProvider _timeProvider;

    public CapabilityDetector(IProcessRunner processRunner,
        IOptions<HostAgentCapabilityOptions> options,
        TimeProvider timeProvider)
    {
        _processRunner = processRunner;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<HostCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        var nvidiaGpuInference = await CommandSucceedsAsync("nvidia-smi", ["-L"], diagnostics, cancellationToken)
            .ConfigureAwait(false);
        var gpuRuntimeConfigured = await CommandSucceedsAsync("nvidia-container-runtime", ["--version"], diagnostics, cancellationToken)
            .ConfigureAwait(false);

        return new HostCapabilitiesDto
        {
            CpuAvailable = Environment.ProcessorCount > 0,
            NvidiaGpuInference = nvidiaGpuInference,
            GpuRuntimeConfigured = gpuRuntimeConfigured,
            AmdGpuStatus = AmdUnsupported,
            RuntimeDiskBytes = GetDirectorySize(_options.Value.RuntimeDataPath),
            ObservedAt = _timeProvider.GetUtcNow(),
            Diagnostics = diagnostics
        };
    }

    private async Task<bool> CommandSucceedsAsync(string fileName,
        IReadOnlyList<string> arguments,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return true;
            }

            diagnostics.Add($"{fileName}:exit:{result.ExitCode}");
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException or Win32Exception)
        {
            diagnostics.Add($"{fileName}:unavailable");
            return false;
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Select(file => new FileInfo(file).Length)
                        .Sum();
    }
}
