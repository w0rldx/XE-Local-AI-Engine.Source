namespace XE_Local_AI_Engine.Client.Services.Diagnostics;

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Builds the <see cref="NodeInfo" /> report shown in the Diagnostics panel and written into the support bundle.</summary>
public interface INodeInfoService
{
    /// <summary>Captures the report. Never throws for a failing provider; the whole capture is bounded to a few seconds.</summary>
    /// <param name="isShellOwned">Whether a desktop shell owns this process; a host-only fact the endpoint passes in.</param>
    /// <param name="ct">The request's cancellation.</param>
    Task<NodeInfo> GetAsync(bool isShellOwned, CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
///     Reads only cached or cheap state: the memoized hardware profile and device audit (<c>forceRefresh: false</c>, so a
///     never-probed host reports an empty profile), the shared live-memory reading, the three installed-runtime records,
///     the in-memory residents and the installed GGUF list. It never calls the benchmark facts capture, which can
///     download a runtime. Every section runs under one shared deadline.
/// </remarks>
public sealed class NodeInfoService : INodeInfoService
{
    /// <summary>The whole capture's deadline; a section still running then is reported as a warning.</summary>
    public static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(5);

    /// <summary>The settings fields the report carries; with <see cref="ExcludedSettings" /> this is the full field review.</summary>
    public static readonly IReadOnlySet<string> IncludedSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(StoredNodeSettings.MaxMessageRequestTimeoutSeconds),
        nameof(StoredNodeSettings.DefaultModelName),
        nameof(StoredNodeSettings.EnableTools),
        nameof(StoredNodeSettings.ToolCapableModels),
        nameof(StoredNodeSettings.HuggingFaceDefaultQuant),
        nameof(StoredNodeSettings.HuggingFaceDiskMarginBytes),
        nameof(StoredNodeSettings.LlamaMaxLoadedProcesses),
        nameof(StoredNodeSettings.LlamaIdleTimeToLiveSeconds),
        nameof(StoredNodeSettings.KeepModelWarmEnabled),
        nameof(StoredNodeSettings.KeepModelWarmModelName),
        nameof(StoredNodeSettings.KeepModelWarmIntervalSeconds),
        nameof(StoredNodeSettings.MaxResponseSizeMb),
        nameof(StoredNodeSettings.RecommendedLlamaCppTag),
        nameof(StoredNodeSettings.OrchestrationIdleTimeoutSeconds),
        nameof(StoredNodeSettings.AgentHomePrepareTimeoutSeconds),
        nameof(StoredNodeSettings.AgentHomeCommandTimeoutSeconds),
        nameof(StoredNodeSettings.AgentHomeMaxSelectedFolderBytes),
        nameof(StoredNodeSettings.AgentHomeMaxPatchBytes),
        nameof(StoredNodeSettings.MaxPendingToolCallAgeMinutes),
        nameof(StoredNodeSettings.DetachedGraceSeconds),
        nameof(StoredNodeSettings.ChatCacheReuse),
        nameof(StoredNodeSettings.SpeculativeMode),
        nameof(StoredNodeSettings.KvCacheType),
        nameof(StoredNodeSettings.SpeculativeDraftModelName),
        nameof(StoredNodeSettings.SpeculativeDraftMaxTokens),
        nameof(StoredNodeSettings.SpeculativeDraftGpuLayers),
        nameof(StoredNodeSettings.RerankerModelName),
        nameof(StoredNodeSettings.AutoEffortFastModelName),
        nameof(StoredNodeSettings.VoiceFeatureEnabled),
        nameof(StoredNodeSettings.CustomToolsEnabled),
        nameof(StoredNodeSettings.ToolRelevanceEnabled),
        nameof(StoredNodeSettings.WebAccessEnabled),
        nameof(StoredNodeSettings.ExternalAccessProfile),
        nameof(StoredNodeSettings.UiMode),
        nameof(StoredNodeSettings.UpdateChannel),
        nameof(StoredNodeSettings.AutoCheckApplicationUpdates),
        nameof(StoredNodeSettings.AutoCheckRuntimeUpdates),
        nameof(StoredNodeSettings.AutoProvisionFirstRunModel),
        nameof(StoredNodeSettings.DefaultVoiceProfile),
        nameof(StoredNodeSettings.ToolApprovalPolicy),
        nameof(StoredNodeSettings.UsageRates),
        nameof(StoredNodeSettings.TranscriptionSelectedModelId),
        nameof(StoredNodeSettings.TranscriptionIdleTimeoutMinutes),
        nameof(StoredNodeSettings.LlamaReadinessTimeoutCapSeconds),
        nameof(StoredNodeSettings.LlamaChatHttpTimeoutSeconds),
        nameof(StoredNodeSettings.LlamaEmbeddingHttpTimeoutSeconds),
        nameof(StoredNodeSettings.LlamaChatCacheRamMiB),
        nameof(StoredNodeSettings.LlamaCpuThreadReserve),
        nameof(StoredNodeSettings.LlamaGpuReservePercent),
        nameof(StoredNodeSettings.LlamaRamReservePercent),
        nameof(StoredNodeSettings.ImageIdleTimeToLiveSeconds),
        nameof(StoredNodeSettings.ModelFitSafetyMarginPercent),
        nameof(StoredNodeSettings.MaxProviderCallsPerInvocation),
        nameof(StoredNodeSettings.CustomToolMaxTimeoutSeconds),
        nameof(StoredNodeSettings.WebFetchTimeoutSeconds),
        nameof(StoredNodeSettings.WebFetchMaxContentChars),
        nameof(StoredNodeSettings.KnowledgeSearchDefaultResults),
        nameof(StoredNodeSettings.KnowledgeSearchMaxResults),
        nameof(StoredNodeSettings.ReasoningBudgetMinimalTokens),
        nameof(StoredNodeSettings.ReasoningBudgetLowTokens),
        nameof(StoredNodeSettings.ReasoningBudgetMediumTokens),
        nameof(StoredNodeSettings.ReasoningBudgetHighTokens),
        nameof(StoredNodeSettings.DefaultReasoningEffort),
        nameof(StoredNodeSettings.ChatOutputCapMode),
        nameof(StoredNodeSettings.ChatOutputCapMaxTokens),
        nameof(StoredNodeSettings.HuggingFaceDownloadConnections),
        nameof(StoredNodeSettings.TranscriptionInferenceTimeoutMinutes),
        nameof(StoredNodeSettings.AgentHomeMaxRunSeconds),
        nameof(StoredNodeSettings.AgentHomeRunRetentionDays),
        nameof(StoredNodeSettings.ContainerRuntimeSelection),
        nameof(StoredNodeSettings.ToolPipelineMaxIterationsPerRequest),
        nameof(StoredNodeSettings.ToolPipelineMaxToolResultChars),
        nameof(StoredNodeSettings.ToolPipelineMaxConsecutiveInvalidToolCalls),
        nameof(StoredNodeSettings.DefaultContextTokens),
        nameof(StoredNodeSettings.ProviderBudgetRecentMessagesToKeep),
        nameof(StoredNodeSettings.ProviderBudgetMaxCumulativeInputTokens),
        nameof(StoredNodeSettings.ContextBudgetRecentTurnKeepCount),
        nameof(StoredNodeSettings.CompactionAutoEnabled),
        nameof(StoredNodeSettings.CompactionAutoCompactPercent),
        nameof(StoredNodeSettings.CompactionRecentMessagesVerbatim),
        nameof(StoredNodeSettings.CompactionDistillEnabled),
        nameof(StoredNodeSettings.MaxInlinedAttachmentChars),
        nameof(StoredNodeSettings.KnowledgeChatTopK),
        nameof(StoredNodeSettings.ProviderRetryEnabled),
        nameof(StoredNodeSettings.ProviderMaxRetries),
        nameof(StoredNodeSettings.SpawnMaxConcurrent),
        nameof(StoredNodeSettings.SpawnMaxCloud),
        nameof(StoredNodeSettings.SpawnQueueWaitSeconds),
        nameof(StoredNodeSettings.KnowledgeAdaptiveRerankingEnabled),
        nameof(StoredNodeSettings.KnowledgeRetrievalLatencyBudgetMs),
        nameof(StoredNodeSettings.KnowledgeScheduledReindexEnabled),
        nameof(StoredNodeSettings.KnowledgeScheduledReindexIntervalMinutes),
        nameof(StoredNodeSettings.KnowledgeAgentToolsEnabled),
        nameof(StoredNodeSettings.AllowCloudModelAccess),
        nameof(StoredNodeSettings.PlaybookAnalysisModelName),
        nameof(StoredNodeSettings.PlaybookEvalModelName),
        nameof(StoredNodeSettings.MemoryExtractionModelName),
        nameof(StoredNodeSettings.ChatRetentionEnabled),
        nameof(StoredNodeSettings.ChatRetentionDays),
        nameof(StoredNodeSettings.AgentExecutionLogRetentionEnabled),
        nameof(StoredNodeSettings.AgentExecutionLogRetentionDays),
        nameof(StoredNodeSettings.NodeDbBackupRetainCount),
        nameof(StoredNodeSettings.BenchmarkKldCacheMaxBytes),
        nameof(StoredNodeSettings.SchedulerHistoryRetentionDays),
        nameof(StoredNodeSettings.ImageMaxLoadedProcesses),
        nameof(StoredNodeSettings.ImageTextEncoderOnGpu),
        nameof(StoredNodeSettings.GraphWorkflowMaxConcurrentRuns),
        nameof(StoredNodeSettings.GraphWorkflowDefaultNodeTimeoutSeconds),
        nameof(StoredNodeSettings.WorkSessionMaxStepsPerRun),
        nameof(StoredNodeSettings.WorkSessionMaxConcurrentSessions),
        nameof(StoredNodeSettings.DevelopmentMaxAttemptDurationSeconds),
        nameof(StoredNodeSettings.DevelopmentEnabled),
        nameof(StoredNodeSettings.SchedulerEnabled),
        nameof(StoredNodeSettings.ExternalAppsEnabled),
        nameof(StoredNodeSettings.WorkSessionsEnabled),
        nameof(StoredNodeSettings.GraphWorkflowsEnabled),
        nameof(StoredNodeSettings.TranscriptionEnabled),
        nameof(StoredNodeSettings.ComputeEnabled),
        nameof(StoredNodeSettings.AgentHomeEnabled),
        nameof(StoredNodeSettings.DevWorkflowsEnabled),
        nameof(StoredNodeSettings.DevelopmentMaxToolCalls),
        nameof(StoredNodeSettings.DevelopmentMaxOutputTokens),
        nameof(StoredNodeSettings.AgentHomeMaxInnerToolCalls),
        nameof(StoredNodeSettings.AgentHomePatchApplyTimeoutSeconds),
        nameof(StoredNodeSettings.AgentHomeRunRetentionMaxRuns),
        nameof(StoredNodeSettings.AgentHomeRunRetentionMaxTotalBytes)
    };

    /// <summary>The settings fields the report never carries; a test fails when a new field is in neither set.</summary>
    /// <remarks>
    ///     <c>MachineKey</c> is the stable local machine identifier; <c>OllamaEndpoint</c> and <c>WebSearchSearxngUrl</c>
    ///     are operator URLs that can carry a host name or <c>user:password@</c> userinfo. No other field is URL- or
    ///     credential-shaped: credentials live in their own encrypted stores, not in this record.
    /// </remarks>
    public static readonly IReadOnlySet<string> ExcludedSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(StoredNodeSettings.MachineKey),
        nameof(StoredNodeSettings.OllamaEndpoint),
        nameof(StoredNodeSettings.WebSearchSearxngUrl)
    };

    private static readonly JsonSerializerOptions SettingsSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IRuntimeDeviceAudit _deviceAudit;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IInstalledRuntimeStore _llamaRuntimeStore;
    private readonly ILlamaServerProcessSupervisor _llamaSupervisor;
    private readonly NodeLaunchContext _launchContext;
    private readonly ILiveMemorySampler _liveMemorySampler;
    private readonly NodeLogLevelSwitch _logLevel;
    private readonly ILogger<NodeInfoService> _logger;
    private readonly RuntimeResidentsService _residents;
    private readonly INodeSettingsAdministrationService _settings;
    private readonly IStableDiffusionInstalledRuntimeStore _stableDiffusionRuntimeStore;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<AppUpdateChannelOptions> _updateOptions;
    private readonly IWhisperInstalledRuntimeStore _whisperRuntimeStore;

    public NodeInfoService(IHardwareProfiler hardwareProfiler,
        IRuntimeDeviceAudit deviceAudit,
        ILiveMemorySampler liveMemorySampler,
        IInstalledRuntimeStore llamaRuntimeStore,
        ILlamaServerProcessSupervisor llamaSupervisor,
        IStableDiffusionInstalledRuntimeStore stableDiffusionRuntimeStore,
        IWhisperInstalledRuntimeStore whisperRuntimeStore,
        RuntimeResidentsService residents,
        IGgufModelStore ggufModelStore,
        INodeSettingsAdministrationService settings,
        IOptions<AppUpdateChannelOptions> updateOptions,
        NodeLaunchContext launchContext,
        TimeProvider timeProvider,
        NodeLogLevelSwitch logLevel,
        ILogger<NodeInfoService> logger)
    {
        ArgumentNullException.ThrowIfNull(hardwareProfiler);
        ArgumentNullException.ThrowIfNull(deviceAudit);
        ArgumentNullException.ThrowIfNull(liveMemorySampler);
        ArgumentNullException.ThrowIfNull(llamaRuntimeStore);
        ArgumentNullException.ThrowIfNull(llamaSupervisor);
        ArgumentNullException.ThrowIfNull(stableDiffusionRuntimeStore);
        ArgumentNullException.ThrowIfNull(whisperRuntimeStore);
        ArgumentNullException.ThrowIfNull(residents);
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(updateOptions);
        ArgumentNullException.ThrowIfNull(launchContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logLevel);
        ArgumentNullException.ThrowIfNull(logger);
        _hardwareProfiler = hardwareProfiler;
        _deviceAudit = deviceAudit;
        _liveMemorySampler = liveMemorySampler;
        _llamaRuntimeStore = llamaRuntimeStore;
        _llamaSupervisor = llamaSupervisor;
        _stableDiffusionRuntimeStore = stableDiffusionRuntimeStore;
        _whisperRuntimeStore = whisperRuntimeStore;
        _residents = residents;
        _ggufModelStore = ggufModelStore;
        _settings = settings;
        _updateOptions = updateOptions;
        _launchContext = launchContext;
        _timeProvider = timeProvider;
        _logLevel = logLevel;
        _logger = logger;
    }

    public async Task<NodeInfo> GetAsync(bool isShellOwned, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(CaptureBudget, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        var warnings = new List<string>();

        var profileTask = CaptureAsync("hardware profile", t => _hardwareProfiler.GetProfileAsync(forceRefresh: false, t), token);
        var auditTask = CaptureAsync("device audit", t => _deviceAudit.GetAuditAsync(forceRefresh: false, t), token);
        var sampleTask = CaptureAsync("live memory", _liveMemorySampler.SampleAsync, token);
        var runtimesTask = CaptureAsync("installed runtimes", ReadRuntimesAsync, token);
        var runningTask = CaptureAsync("running models", _llamaSupervisor.CheckHealthAsync, token);
        var modelsTask = CaptureAsync("installed models", _ggufModelStore.ListInstalledModelsAsync, token);
        var settingsTask = CaptureAsync("node settings", t => _settings.GetTrustedSettingsAsync(t), token);
        var residentsTask = CaptureAsync("runtime residents", _residents.GetResidentsAsync, token);

        var residents = await Collect(residentsTask, warnings);
        var profile = await Collect(profileTask, warnings);
        var audit = await Collect(auditTask, warnings);
        var sample = await Collect(sampleTask, warnings);
        var runtimes = await Collect(runtimesTask, warnings);
        var running = await Collect(runningTask, warnings);
        var models = await Collect(modelsTask, warnings);
        var settings = await Collect(settingsTask, warnings);
        ct.ThrowIfCancellationRequested();

        var options = _updateOptions.Value;
        var version = typeof(NodeInfoService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return new NodeInfo
        {
            CapturedAtUtc = _timeProvider.GetUtcNow(),
            Version = version,
            Commit = plus >= 0 && plus < version.Length - 1 ? version[(plus + 1)..] : null,
            Flavour = options.Channel,
            SelectedChannel = settings?.UpdateChannel ?? AppUpdateChannelNames.ToWire(options.DefaultChannel),
            DefaultChannel = AppUpdateChannelNames.ToWire(options.DefaultChannel),
            RepositoryUrl = options.SourcePolicy?.GitHubRepositoryUrl,
            IsLocalMode = _launchContext.IsLocalMode,
            IsShellOwned = isShellOwned,
            VerboseLogging = _logLevel.Verbose,
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeFramework = RuntimeInformation.FrameworkDescription,
            CpuModel = Capture("cpu model", HostCpuModel.TryRead, warnings),
            CpuCores = profile?.CpuCores,
            TotalRamBytes = profile?.TotalRamBytes,
            AvailableRamBytes = sample?.AvailableRamBytes ?? profile?.AvailableRamBytes,
            FreeDiskBytes = profile?.FreeDiskBytes,
            GpuVendor = profile?.GpuVendor.ToString(),
            InferenceBackend = audit?.InferenceBackend,
            CpuFallback = audit?.CpuFallback,
            Gpus = audit?.Devices.Select(static device => new NodeInfoGpu
            {
                Name = device.Name,
                TotalBytes = device.TotalBytes,
                FreeBytes = device.FreeBytes
            }).ToArray(),
            LiveGpuMemory = sample?.Gpus
                                  .Select(static gpu => new NodeInfoGpuMemory
                                  {
                                      Index = gpu.Index,
                                      TotalVramBytes = gpu.TotalVramBytes,
                                      UsedVramBytes = gpu.UsedVramBytes,
                                      AvailableVramBytes = gpu.AvailableVramBytes
                                  })
                                  .ToArray(),
            Runtimes = runtimes,
            Residents = residents?.Select(static resident => new NodeInfoResident
                                 {
                                     Runtime = resident.Runtime.ToString(),
                                     ModelId = resident.ModelId,
                                     State = resident.State.ToString(),
                                     Backend = resident.Backend?.ToString()
                                 })
                                 .ToArray(),
            RunningModels = running?.Select(static process => new NodeInfoRunningModel
                                   {
                                       ModelName = process.ModelName,
                                       Role = process.Role switch
                                       {
                                           ModelRole.Embedding => "embedding",
                                           ModelRole.Reranker => "reranker",
                                           _ => "chat"
                                       },
                                       State = (process.HasExited, process.IsResponsive) switch
                                       {
                                           (true, _) => "exited",
                                           (false, true) => "responsive",
                                           _ => "unresponsive"
                                       },
                                       IsBusy = process.IsBusy,
                                       IsTransient = process.IsTransient,
                                       LastUsedUtc = process.LastUsedUtc
                                   })
                                   .ToArray(),
            Models = models?.Select(static model => new NodeInfoModel
            {
                Name = model.ModelName,
                Provider = model.ProviderName,
                SizeBytes = model.SizeBytes
            }).ToArray(),
            Settings = settings is null ? null : RenderSettings(settings),
            UptimeSeconds = ReadUptimeSeconds(),
            Warnings = warnings
        };
    }

    /// <summary>Renders the <see cref="IncludedSettings" /> as invariant text keyed by their camel-case wire name.</summary>
    public static IReadOnlyDictionary<string, string?> RenderSettings(StoredNodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.SerializeToNode(settings, SettingsSerializerOptions)!.AsObject();
        var rendered = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in IncludedSettings)
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(name);
            var node = json[key];
            rendered[key] = node is JsonValue value && value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : node?.ToJsonString();
        }

        return rendered;
    }

    private async Task<IReadOnlyList<NodeInfoRuntime>> ReadRuntimesAsync(CancellationToken ct)
    {
        var runtimes = new List<NodeInfoRuntime>();

        // Build paths and checksums are left out on purpose: a path names the user's directories.
        var llama = await _llamaRuntimeStore.ReadAsync(ct);
        if (llama is not null)
        {
            runtimes.Add(new NodeInfoRuntime
            {
                Kind = "llama-cpp",
                Tag = llama.Tag,
                Backend = llama.Variant.ToString(),
                InstalledAtUtc = llama.InstalledAtUtc,
                SourceCommit = llama.SourceCommit,
                IsValid = true
            });
        }

        var stableDiffusion = await _stableDiffusionRuntimeStore.ReadAsync(ct);
        if (stableDiffusion is not null)
        {
            runtimes.Add(new NodeInfoRuntime
            {
                Kind = "stable-diffusion-cpp",
                Backend = stableDiffusion.DesiredBackend.ToString(),
                InstalledAtUtc = stableDiffusion.InstalledAtUtc,
                SourceCommit = stableDiffusion.SourceCommit,
                IsValid = stableDiffusion.Validity == StableDiffusionInstalledRuntimeValidity.Active
            });
        }

        var whisper = await _whisperRuntimeStore.ReadAsync(ct);
        if (whisper is not null)
        {
            runtimes.Add(new NodeInfoRuntime
            {
                Kind = "whisper-cpp",
                Backend = whisper.DesiredBackend.ToString(),
                InstalledAtUtc = whisper.InstalledAtUtc,
                SourceCommit = whisper.SourceCommit,
                IsValid = whisper.Validity == WhisperInstalledRuntimeValidity.Active
            });
        }

        return runtimes;
    }

    private long ReadUptimeSeconds()
    {
        using var process = Process.GetCurrentProcess();
        var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        return Math.Max(0, (long)(_timeProvider.GetUtcNow() - started).TotalSeconds);
    }

    // Starts a section; WaitAsync bounds a provider that ignores its token.
    private static async Task<(string Section, T? Value, string? Failure)> CaptureAsync<T>(string section,
        Func<CancellationToken, Task<T>> capture,
        CancellationToken token)
        where T : class
    {
        try
        {
            return (section, await capture(token).WaitAsync(token), null);
        }
        catch (OperationCanceledException)
        {
            return (section, null, "did not answer in time");
        }
        catch (Exception exception)
        {
            return (section, null, exception.GetType().Name);
        }
    }

    private async Task<T?> Collect<T>(Task<(string Section, T? Value, string? Failure)> pending, List<string> warnings)
        where T : class
    {
        var (section, value, failure) = await pending;
        if (failure is not null)
        {
            _logger.LogWarning("Node info: the {Section} section is unavailable ({Failure}).", section, failure);
            warnings.Add($"{section}: {failure}");
        }

        return value;
    }

    private T? Capture<T>(string section, Func<T?> capture, List<string> warnings)
        where T : class
    {
        try
        {
            return capture();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Node info: the {Section} section is unavailable.", section);
            warnings.Add($"{section}: {exception.GetType().Name}");
            return null;
        }
    }
}
