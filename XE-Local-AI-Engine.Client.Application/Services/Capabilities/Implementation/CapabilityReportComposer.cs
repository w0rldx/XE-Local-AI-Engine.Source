namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Composes the <see cref="ClientCapabilities" /> report: local hardware detection plus assembly of the cloud and
///     local capability payloads (diagnostics, supported capabilities, system score). Collaborator behind
///     <see cref="CapabilityReporter" />; holds no runtime state.
/// </summary>
internal sealed class CapabilityReportComposer
{
    private const int CapabilitySchemaVersion = 2;
    private const string DiagnosticMissingCuda = "missing-cuda";
    private const string DiagnosticMissingGpu = "missing-gpu";
    private const string DiagnosticUnknownInventory = "unknown-inventory";
    private static readonly string[] VisionModelMarkers = ["llava", "bakllava", "vision", "moondream", "minicpm-v"];
    private static readonly string[] BaseCapabilities = ["text"];

    // AgentHome MVP capability strings (capability flag). Advertised only when AgentHome:Enabled=true so a node
    // never claims sandbox/workspace/patch/memory support it cannot serve. The normalizer trims/dedupes/
    // sorts these, and the server stores SupportedCapabilities as a free-form JSON list (no schema bump).
    private static readonly string[] AgentHomeCapabilities =
    [
        "agent-home",
        "sandbox-process",
        "runtime-dotnet-agent-home",
        "workspace-copy",
        "patch-export",
        "memory-proposals"
    ];

    private readonly bool _agentHomeEnabled;
    private readonly ILogger<CapabilityReportComposer> _logger;

    public CapabilityReportComposer(IConfiguration configuration, ILogger<CapabilityReportComposer> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _agentHomeEnabled = configuration.GetValue<bool>("AgentHome:Enabled");
    }

    /// <summary>Gathers local hardware facts (RAM, GPU, CPU) used by both cloud and local report assembly.</summary>
    public async Task<HardwareSnapshot> DetectHardwareAsync(CancellationToken cancellationToken)
    {
        var ramMb = await DetectRamMbAsync(cancellationToken).ConfigureAwait(false);
        var gpuInfo = await TryDetectGpuInfoAsync(cancellationToken).ConfigureAwait(false);
        var cpuClass = await DetectCpuClassAsync(cancellationToken).ConfigureAwait(false);
        return new HardwareSnapshot(ramMb, gpuInfo, cpuClass);
    }

    /// <summary>
    ///     Assembles the capability payload for an Azure Foundry cloud node from the stored connection. Surfaces every
    ///     configured deployment (multi-model) and works for both API-key and managed-identity auth (no key needed).
    /// </summary>
    public static ClientCapabilities ComposeCloud(StoredAzureFoundryConnection connection,
        StoredNodeSettings nodeSettings,
        HardwareSnapshot hardware,
        DateTimeOffset detectedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var deploymentNames = connection.Models
                                        .Select(static model => model.DeploymentName?.Trim() ?? string.Empty)
                                        .Where(static name => !string.IsNullOrWhiteSpace(name))
                                        .ToArray();
        var activeModel = deploymentNames.Length > 0 ? deploymentNames[0] : string.Empty;

        return new ClientCapabilities
        {
            SchemaVersion = CapabilitySchemaVersion,
            RamMb = hardware.RamMb,
            VramMb = hardware.GpuInfo?.VramMb,
            CudaAvailable = hardware.GpuInfo?.CudaAvailable ?? false,
            GpuName = hardware.GpuInfo?.GpuName,
            CpuClass = hardware.CpuClass,
            SystemScoreClass = "Cloud",
            NodeType = "Cloud",
            CloudProviderName = CloudProviderOptions.ProviderAzureFoundry,
            ManagementMode = "unknown",
            LastCapabilityReportAt = detectedAt,
            Diagnostics = NormalizeDiagnostics(BuildHardwareDiagnostics(hardware.GpuInfo)),
            InstalledModels = deploymentNames,
            InstalledModelMetadata =
            [
                .. deploymentNames.Select(static name => new ClientModelMetadata
                {
                    Name = name,
                    Digest = null,
                    MaxContextTokens = null
                })
            ],
            SupportedCapabilities = ["cloud", "text"],
            ActiveModel = activeModel,
            ActiveModelExpiresAt = null,
            MaxMessageRequestTimeoutSeconds = nodeSettings.MaxMessageRequestTimeoutSeconds
        };
    }

    /// <summary>Assembles the capability payload for a local Ollama node from probed runtime/model state.</summary>
    public ClientCapabilities ComposeLocal(HardwareSnapshot hardware,
        OllamaRuntimeStatus ollamaStatus,
        InstalledModelInventoryResult inventory,
        IReadOnlyList<ClientModelMetadata> installedModelMetadata,
        ActiveModelInfo activeModel,
        StoredNodeSettings nodeSettings,
        DateTimeOffset detectedAt)
    {
        var diagnostics = BuildHardwareDiagnostics(hardware.GpuInfo);
        diagnostics.AddRange(ollamaStatus.Diagnostics);
        diagnostics.AddRange(inventory.Diagnostics);
        var installedModels = inventory.Models.Select(model => model.Name).ToArray();
        var supportedCapabilities = DetermineSupportedCapabilities(installedModels, _agentHomeEnabled);
        var ollamaReachable = ollamaStatus.Reachable && inventory.OllamaQuerySucceeded;
        if (installedModels.Length == 0)
        {
            diagnostics.Add(DiagnosticUnknownInventory);
        }

        return new ClientCapabilities
        {
            SchemaVersion = CapabilitySchemaVersion,
            RamMb = hardware.RamMb,
            VramMb = hardware.GpuInfo?.VramMb,
            CudaAvailable = hardware.GpuInfo?.CudaAvailable ?? false,
            GpuName = hardware.GpuInfo?.GpuName,
            CpuClass = hardware.CpuClass,
            SystemScoreClass = CalculateSystemScoreClass(hardware.RamMb, hardware.GpuInfo?.VramMb, hardware.GpuInfo?.CudaAvailable ?? false),
            OllamaReachable = ollamaReachable,
            OllamaVersion = ollamaReachable ? ollamaStatus.Version : null,
            ManagementMode = ollamaReachable ? "unmanaged" : "unknown",
            LastCapabilityReportAt = detectedAt,
            Diagnostics = NormalizeDiagnostics(diagnostics),
            InstalledModels = installedModels,
            InstalledModelMetadata = installedModelMetadata,
            SupportedCapabilities = supportedCapabilities,
            ActiveModel = activeModel.Name,
            ActiveModelExpiresAt = activeModel.ExpiresAt,
            MaxMessageRequestTimeoutSeconds = nodeSettings.MaxMessageRequestTimeoutSeconds
        };
    }

    private static IReadOnlyList<string> DetermineSupportedCapabilities(IReadOnlyList<string> installedModels, bool agentHomeEnabled)
    {
        var capabilities = new HashSet<string>(BaseCapabilities, StringComparer.OrdinalIgnoreCase);

        if (installedModels.Any(HasVisionSupport))
        {
            capabilities.Add("vision");
        }

        if (agentHomeEnabled)
        {
            capabilities.UnionWith(AgentHomeCapabilities);
        }

        return capabilities.OrderBy(capability => capability, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasVisionSupport(string modelName)
    {
        return VisionModelMarkers.Any(marker =>
            modelName.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> BuildHardwareDiagnostics(GpuInfo? gpuInfo)
    {
        var diagnostics = new List<string>();
        if (gpuInfo is null)
        {
            diagnostics.Add(DiagnosticMissingGpu);
            diagnostics.Add(DiagnosticMissingCuda);
            return diagnostics;
        }

        if (!gpuInfo.CudaAvailable)
        {
            diagnostics.Add(DiagnosticMissingCuda);
        }

        return diagnostics;
    }

    private static IReadOnlyList<string> NormalizeDiagnostics(IEnumerable<string> diagnostics)
    {
        return diagnostics
               .Where(static diagnostic => !string.IsNullOrWhiteSpace(diagnostic))
               .Select(static diagnostic => diagnostic.Trim())
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(static diagnostic => diagnostic, StringComparer.OrdinalIgnoreCase)
               .ToArray();
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

            var parts = line.Split(separator: ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

            var separatorIndex = line.IndexOf(value: ':', StringComparison.Ordinal);
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
                    CreateNoWindow = true
                }
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
            return primaryGpu with
            {
                CudaAvailable = true
            };
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _logger.LogDebug(exception, "nvidia-smi is not available for GPU detection.");
            return null;
        }
    }

    private static GpuInfo? ParseGpuLine(string line)
    {
        var parts = line.Split(separator: ',', count: 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        long? vramMb = null;
        if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVramMb))
        {
            vramMb = parsedVramMb;
        }

        return new GpuInfo(parts[0], vramMb, CudaAvailable: false);
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
}
