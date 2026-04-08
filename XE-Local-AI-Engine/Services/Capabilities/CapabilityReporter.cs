namespace XE_Local_AI_Engine.Services.Capabilities
{
    using System;
    using System.ComponentModel;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.AI;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using OllamaSharp;
    using XE_Local_AI_Engine.Models;
    using XE_Local_AI_Engine.Services.Connection;

    public sealed class CapabilityReporter : ICapabilityReporter
    {
        private static readonly string[] VisionModelMarkers = ["llava", "bakllava", "vision", "moondream", "minicpm-v"];
        private static readonly string[] BaseCapabilities = ["text"];

        private readonly OllamaApiClient _ollamaClient;
        private readonly IWorkerHubConnection _hubConnection;
        private readonly ILogger<CapabilityReporter> _logger;
        private readonly string _defaultModel;

        public CapabilityReporter(
            IChatClient chatClient,
            IConfiguration configuration,
            IWorkerHubConnection hubConnection,
            ILogger<CapabilityReporter> logger)
        {
            ArgumentNullException.ThrowIfNull(chatClient);
            ArgumentNullException.ThrowIfNull(configuration);
            _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _ollamaClient = chatClient as OllamaApiClient
                ?? throw new InvalidOperationException("The registered IChatClient must be an OllamaApiClient for capability reporting.");
            _defaultModel = configuration.GetValue<string>("Ollama:ChatModel")
                ?? throw new InvalidOperationException("Ollama:ChatModel is required for capability reporting.");
        }

        public async Task<ClientCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ramMb = await DetectRamMbAsync(cancellationToken).ConfigureAwait(false);
            var gpuInfo = await TryDetectGpuInfoAsync(cancellationToken).ConfigureAwait(false);
            var installedModels = await GetInstalledModelNamesAsync(cancellationToken).ConfigureAwait(false);
            var supportedCapabilities = DetermineSupportedCapabilities(installedModels);
            var cpuClass = await DetectCpuClassAsync(cancellationToken).ConfigureAwait(false);

            return new ClientCapabilities
            {
                RamMb = ramMb,
                VramMb = gpuInfo?.VramMb,
                CudaAvailable = gpuInfo?.CudaAvailable ?? false,
                GpuName = gpuInfo?.GpuName,
                CpuClass = cpuClass,
                SystemScoreClass = CalculateSystemScoreClass(ramMb, gpuInfo?.VramMb, gpuInfo?.CudaAvailable ?? false),
                InstalledModels = installedModels,
                SupportedCapabilities = supportedCapabilities,
            };
        }

        public async Task ReportToApiAsync(CancellationToken cancellationToken = default)
        {
            var capabilities = await DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            await _hubConnection.SendCapabilitiesAsync(capabilities, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Reported worker capabilities to API with {ModelCount} installed model(s).",
                capabilities.InstalledModels.Count);
        }

        public async Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!await _ollamaClient.IsRunningAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    _logger.LogWarning("Ollama is not reachable during capability preflight.");
                    return false;
                }
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Ollama preflight failed because the local endpoint is unreachable.");
                return false;
            }

            var installedModels = await GetInstalledModelNamesAsync(cancellationToken).ConfigureAwait(false);
            if (installedModels.Count == 0)
            {
                _logger.LogWarning("Ollama is reachable but no local models are installed.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(modelName))
            {
                return installedModels.Contains(_defaultModel, StringComparer.OrdinalIgnoreCase);
            }

            if (installedModels.Contains(modelName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            var canFallback = installedModels.Contains(_defaultModel, StringComparer.OrdinalIgnoreCase);
            if (canFallback)
            {
                _logger.LogWarning(
                    "Requested model '{RequestedModel}' not available, using fallback '{FallbackModel}'.",
                    modelName,
                    _defaultModel);
            }
            else
            {
                _logger.LogWarning(
                    "Requested model '{RequestedModel}' is unavailable and fallback model '{FallbackModel}' is not installed.",
                    modelName,
                    _defaultModel);
            }

            return canFallback;
        }

        private async Task<IReadOnlyList<string>> GetInstalledModelNamesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var models = await _ollamaClient.ListLocalModelsAsync(CancellationToken.None).ConfigureAwait(false);
                return models
                    .Select(model => model.Name?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToArray();
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Failed to query installed Ollama models.");
                return [];
            }
        }

        private static IReadOnlyList<string> DetermineSupportedCapabilities(IReadOnlyList<string> installedModels)
        {
            var capabilities = new HashSet<string>(BaseCapabilities, StringComparer.OrdinalIgnoreCase);

            if (installedModels.Any(HasVisionSupport))
            {
                capabilities.Add("vision");
            }

            return capabilities.OrderBy(capability => capability, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool HasVisionSupport(string modelName)
        {
            return VisionModelMarkers.Any(marker =>
                modelName.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<long?> DetectRamMbAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var memoryInfo = GC.GetGCMemoryInfo();
            if (memoryInfo.TotalAvailableMemoryBytes > 0)
            {
                return memoryInfo.TotalAvailableMemoryBytes / (1024 * 1024);
            }

            if (!OperatingSystem.IsLinux() || !File.Exists("/proc/meminfo"))
            {
                return null;
            }

            var memInfo = await File.ReadAllLinesAsync("/proc/meminfo", cancellationToken).ConfigureAwait(false);
            foreach (var line in memInfo)
            {
                if (!line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib))
                {
                    return kib / 1024;
                }
            }

            return null;
        }

        private static async Task<string?> DetectCpuClassAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var logicalCores = Environment.ProcessorCount;
            var cpuModel = await TryReadLinuxCpuModelAsync(cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(cpuModel)
                ? $"{logicalCores} logical cores"
                : $"{cpuModel} ({logicalCores} logical cores)";
        }

        private static async Task<string?> TryReadLinuxCpuModelAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/proc/cpuinfo"))
            {
                return null;
            }

            var cpuInfo = await File.ReadAllLinesAsync("/proc/cpuinfo", cancellationToken).ConfigureAwait(false);
            foreach (var line in cpuInfo)
            {
                if (!line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
                if (separatorIndex < 0 || separatorIndex == line.Length - 1)
                {
                    continue;
                }

                var model = line[(separatorIndex + 1)..].Trim();
                return string.IsNullOrWhiteSpace(model) ? null : model;
            }

            return null;
        }

        private async Task<GpuInfo?> TryDetectGpuInfoAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "nvidia-smi",
                        Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };

                if (!process.Start())
                {
                    return null;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        _logger.LogDebug("nvidia-smi exited with code {ExitCode}: {Error}", process.ExitCode, error.Trim());
                    }

                    return null;
                }

                var entries = output
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseGpuLine)
                    .Where(info => info is not null)
                    .Cast<GpuInfo>()
                    .ToArray();

                if (entries.Length == 0)
                {
                    return null;
                }

                var primaryGpu = entries.OrderByDescending(entry => entry.VramMb ?? 0).First();
                return primaryGpu with { CudaAvailable = true };
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                _logger.LogDebug(exception, "nvidia-smi is not available for GPU detection.");
                return null;
            }
        }

        private static GpuInfo? ParseGpuLine(string line)
        {
            var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                return null;
            }

            long? vramMb = null;
            if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVramMb))
            {
                vramMb = parsedVramMb;
            }

            return new GpuInfo(parts[0], vramMb, false);
        }

        private static string? CalculateSystemScoreClass(long? ramMb, long? vramMb, bool cudaAvailable)
        {
            if (ramMb is > 32768 && vramMb is > 12288 && cudaAvailable)
            {
                return "High";
            }

            if ((ramMb is >= 16384 && ramMb <= 32768) || (vramMb is >= 6144 && vramMb <= 12288))
            {
                return "Good";
            }

            if ((ramMb is >= 8192 && ramMb < 16384) || (vramMb is > 0 && vramMb < 6144))
            {
                return "Medium";
            }

            if (ramMb is < 8192 || vramMb is null or <= 0)
            {
                return "Low";
            }

            return null;
        }

        private sealed record GpuInfo(string GpuName, long? VramMb, bool CudaAvailable);
    }
}
