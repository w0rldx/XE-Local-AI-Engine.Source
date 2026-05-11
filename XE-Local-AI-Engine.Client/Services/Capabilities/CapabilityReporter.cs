namespace XE_Local_AI_Engine.Client.Services.Capabilities;

using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class CapabilityReporter : ICapabilityReporter, IDisposable
{
    private const int CapabilitySchemaVersion = 1;
    private const string DiagnosticMissingCuda = "missing-cuda";
    private const string DiagnosticMissingGpu = "missing-gpu";
    private const string DiagnosticOllamaUnreachable = "ollama-unreachable";
    private const string DiagnosticUnknownInventory = "unknown-inventory";
    private static readonly TimeSpan InstalledModelsCacheLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReportThrottleInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] VisionModelMarkers = ["llava", "bakllava", "vision", "moondream", "minicpm-v"];
    private static readonly string[] BaseCapabilities = ["text"];

    private static readonly string[] ConfiguredModelKeys =
    [
        "Agent:LocalChat:DefaultModel",
        "Ollama:ChatModel",
        "Aspire:OllamaSharp:chat:SelectedModel",
        "Aspire:OllamaSharp:embeddings:SelectedModel"
    ];

    private static readonly string[] ModelConnectionStringNames = ["chat", "embeddings"];
    private readonly ICloudCredentialStore _cloudCredentialStore;
    private readonly IReadOnlyList<string> _configuredModelNames;
    private readonly string _defaultModel;
    private readonly IWorkerHubConnection _hubConnection;
    private readonly object _installedModelsCacheSync = new();
    private readonly ILogger<CapabilityReporter> _logger;
    private readonly INodeSettingsStore _nodeSettingsStore;

    private readonly IOllamaApiClient _ollamaClient;
    private readonly SemaphoreSlim _reportSync = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private CachedInstalledModels? _installedModelsCache;
    private DateTimeOffset? _lastReportStartedAt;

    public CapabilityReporter(IOllamaApiClient ollamaClient,
        ICloudCredentialStore cloudCredentialStore,
        INodeSettingsStore nodeSettingsStore,
        IConfiguration configuration,
        IWorkerHubConnection hubConnection,
        TimeProvider timeProvider,
        ILogger<CapabilityReporter> logger)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        ArgumentNullException.ThrowIfNull(configuration);
        _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _defaultModel = configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                        ?? configuration.GetValue<string>("Ollama:ChatModel")
                        ?? throw new InvalidOperationException("Agent:LocalChat:DefaultModel is required for capability reporting.");
        _configuredModelNames = ResolveConfiguredModelNames(configuration);
    }

    public async Task<ClientCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ramMb = await DetectRamMbAsync(cancellationToken).ConfigureAwait(false);
        var gpuInfo = await TryDetectGpuInfoAsync(cancellationToken).ConfigureAwait(false);
        var cpuClass = await DetectCpuClassAsync(cancellationToken).ConfigureAwait(false);
        var cloudCredentials = await _cloudCredentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var detectedAt = _timeProvider.GetUtcNow();

        if (cloudCredentials is not null
            && string.Equals(cloudCredentials.ProviderName, CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase))
        {
            return CreateCloudCapabilities(cloudCredentials, nodeSettings, ramMb, gpuInfo, cpuClass, detectedAt);
        }

        var diagnostics = BuildHardwareDiagnostics(gpuInfo);
        var ollamaStatus = await DetectOllamaRuntimeAsync(cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(ollamaStatus.Diagnostics);
        var installedModels = await GetInstalledModelNamesAsync(cancellationToken).ConfigureAwait(false);
        var supportedCapabilities = DetermineSupportedCapabilities(installedModels);
        var activeModel = await DetectActiveModelAsync(cancellationToken).ConfigureAwait(false);
        if (installedModels.Count == 0)
        {
            diagnostics.Add(DiagnosticUnknownInventory);
        }

        return new ClientCapabilities
        {
            SchemaVersion = CapabilitySchemaVersion,
            RamMb = ramMb,
            VramMb = gpuInfo?.VramMb,
            CudaAvailable = gpuInfo?.CudaAvailable ?? false,
            GpuName = gpuInfo?.GpuName,
            CpuClass = cpuClass,
            SystemScoreClass = CalculateSystemScoreClass(ramMb, gpuInfo?.VramMb, gpuInfo?.CudaAvailable ?? false),
            OllamaReachable = ollamaStatus.Reachable,
            OllamaVersion = ollamaStatus.Version,
            ManagementMode = ollamaStatus.Reachable ? "unmanaged" : "unknown",
            LastCapabilityReportAt = detectedAt,
            Diagnostics = NormalizeDiagnostics(diagnostics),
            InstalledModels = installedModels,
            SupportedCapabilities = supportedCapabilities,
            ActiveModel = activeModel.Name,
            ActiveModelExpiresAt = activeModel.ExpiresAt,
            MaxMessageRequestTimeoutSeconds = nodeSettings.MaxMessageRequestTimeoutSeconds
        };
    }

    public async Task ReportToApiAsync(CancellationToken cancellationToken = default)
    {
        if (!await _reportSync.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping capability report because another report is already in progress.");
            return;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_lastReportStartedAt is not null && now - _lastReportStartedAt.Value < ReportThrottleInterval)
            {
                _logger.LogDebug("Skipping capability report because the last report started at {LastReportStartedAt}.", _lastReportStartedAt);
                return;
            }

            _lastReportStartedAt = now;
            var capabilities = await DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            await _hubConnection.SendCapabilitiesAsync(capabilities, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Reported worker capabilities to API with {ModelCount} installed model(s).",
                capabilities.InstalledModels.Count);
        }
        finally
        {
            _reportSync.Release();
        }
    }

    public async Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!await _ollamaClient.IsRunningAsync(cancellationToken).ConfigureAwait(false))
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
            _logger.LogWarning("Requested model '{RequestedModel}' not available, using fallback '{FallbackModel}'.",
                modelName,
                _defaultModel);
        }
        else
        {
            _logger.LogWarning("Requested model '{RequestedModel}' is unavailable and fallback model '{FallbackModel}' is not installed.",
                modelName,
                _defaultModel);
        }

        return canFallback;
    }

    public void Dispose()
    {
        _reportSync.Dispose();
    }

    private static ClientCapabilities CreateCloudCapabilities(StoredCloudCredentials credentials,
        StoredNodeSettings nodeSettings,
        long? ramMb,
        GpuInfo? gpuInfo,
        string? cpuClass,
        DateTimeOffset detectedAt)
    {
        var deploymentName = credentials.DeploymentName.Trim();

        return new ClientCapabilities
        {
            SchemaVersion = CapabilitySchemaVersion,
            RamMb = ramMb,
            VramMb = gpuInfo?.VramMb,
            CudaAvailable = gpuInfo?.CudaAvailable ?? false,
            GpuName = gpuInfo?.GpuName,
            CpuClass = cpuClass,
            SystemScoreClass = "Cloud",
            NodeType = "Cloud",
            CloudProviderName = CloudProviderOptions.ProviderAzureFoundry,
            ManagementMode = "unknown",
            LastCapabilityReportAt = detectedAt,
            Diagnostics = NormalizeDiagnostics(BuildHardwareDiagnostics(gpuInfo)),
            InstalledModels = [deploymentName],
            SupportedCapabilities = ["cloud", "text"],
            ActiveModel = deploymentName,
            ActiveModelExpiresAt = null,
            MaxMessageRequestTimeoutSeconds = nodeSettings.MaxMessageRequestTimeoutSeconds
        };
    }

    private async Task<IReadOnlyList<string>> GetInstalledModelNamesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cachedModels = TryGetCachedInstalledModels();
        if (cachedModels is not null)
        {
            return cachedModels;
        }

        try
        {
            var models = await _ollamaClient.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
            var discoveredModelNames = models
                                       .Select(model => NormalizeModelName(model.Name))
                                       .Where(name => !string.IsNullOrWhiteSpace(name))
                                       .Cast<string>()
                                       .ToArray();
            var normalizedModels = models
                                   .Select(model => NormalizeModelName(model.Name))
                                   .Concat(_configuredModelNames)
                                   .Where(name => !string.IsNullOrWhiteSpace(name))
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                   .Cast<string>()
                                   .ToArray();

            _logger.LogInformation(
                "Detected {DiscoveredModelCount} Ollama model(s), {ConfiguredModelCount} configured model fallback(s), reporting {ReportedModelCount} installed model(s): {ReportedModels}.",
                discoveredModelNames.Length,
                _configuredModelNames.Count,
                normalizedModels.Length,
                string.Join(", ", normalizedModels));

            CacheInstalledModels(normalizedModels);
            return normalizedModels;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Failed to query installed Ollama models. Reporting {ConfiguredModelCount} configured model fallback(s): {ConfiguredModels}.",
                _configuredModelNames.Count,
                string.Join(", ", _configuredModelNames));
            return _configuredModelNames;
        }
    }

    private async Task<OllamaRuntimeStatus> DetectOllamaRuntimeAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();

        try
        {
            if (!await _ollamaClient.IsRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                diagnostics.Add(DiagnosticOllamaUnreachable);
                return new OllamaRuntimeStatus(false, null, diagnostics);
            }

            var version = await _ollamaClient.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            return new OllamaRuntimeStatus(true, NormalizeModelName(version?.ToString()), diagnostics);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Ollama runtime detection failed because the endpoint is unreachable.");
            diagnostics.Add(DiagnosticOllamaUnreachable);
            return new OllamaRuntimeStatus(false, null, diagnostics);
        }
    }

    private static IReadOnlyList<string> ResolveConfiguredModelNames(IConfiguration configuration)
    {
        var configuredModelNames = ConfiguredModelKeys
                                   .Select(configuration.GetValue<string>)
                                   .Select(NormalizeModelName);
        var connectionStringModelNames = ModelConnectionStringNames
                                         .Select(configuration.GetConnectionString)
                                         .Select(TryExtractModelName);

        return configuredModelNames
               .Concat(connectionStringModelNames)
               .Where(modelName => !string.IsNullOrWhiteSpace(modelName))
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(modelName => modelName, StringComparer.OrdinalIgnoreCase)
               .Cast<string>()
               .ToArray();
    }

    private static string? TryExtractModelName(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var connectionStringBuilder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        return connectionStringBuilder.TryGetValue("Model", out var modelValue)
               && modelValue is string modelName
            ? NormalizeModelName(modelName)
            : null;
    }

    private async Task<ActiveModelInfo> DetectActiveModelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var runningModels = await _ollamaClient.ListRunningModelsAsync(cancellationToken).ConfigureAwait(false);
            var active = runningModels.FirstOrDefault();
            if (active is null)
            {
                return ActiveModelInfo.None;
            }

            var modelName = NormalizeModelName(active.Name) ?? NormalizeModelName(active.ModelName);
            if (modelName is null)
            {
                return ActiveModelInfo.None;
            }

            return new ActiveModelInfo(modelName, NormalizeExpiresAt(active.ExpiresAt));
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Failed to query running Ollama models.");
            return ActiveModelInfo.None;
        }
    }

    private static string? NormalizeModelName(string? modelName)
    {
        var normalized = modelName?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static DateTimeOffset? NormalizeExpiresAt(object? expiresAt)
    {
        return expiresAt switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(value),
            string value when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
            _ => null
        };
    }

    private IReadOnlyList<string>? TryGetCachedInstalledModels()
    {
        lock (_installedModelsCacheSync)
        {
            if (_installedModelsCache is null)
            {
                return null;
            }

            if (_timeProvider.GetUtcNow() >= _installedModelsCache.ExpiresAt)
            {
                _installedModelsCache = null;
                return null;
            }

            return _installedModelsCache.Models;
        }
    }

    private void CacheInstalledModels(IReadOnlyList<string> models)
    {
        lock (_installedModelsCacheSync)
        {
            _installedModelsCache = new CachedInstalledModels(models, _timeProvider.GetUtcNow().Add(InstalledModelsCacheLifetime));
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

    private sealed record CachedInstalledModels(IReadOnlyList<string> Models, DateTimeOffset ExpiresAt);

    private sealed record OllamaRuntimeStatus(bool Reachable, string? Version, IReadOnlyList<string> Diagnostics);

    private sealed record ActiveModelInfo(string? Name, DateTimeOffset? ExpiresAt)
    {
        public static ActiveModelInfo None { get; } = new(null, null);
    }

    private sealed record GpuInfo(string GpuName, long? VramMb, bool CudaAvailable);
}
