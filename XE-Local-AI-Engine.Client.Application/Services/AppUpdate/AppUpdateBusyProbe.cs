namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Lists the in-flight work an update restart would stop, read from the services that already track it.</summary>
/// <remarks>
///     A best-effort snapshot for the operator's decision, not a lock: work that starts between this read and the
///     host stop is still lost. The GPU gate and the invocation runner expose only a kind, so those items carry no name.
/// </remarks>
public sealed class AppUpdateBusyProbe
{
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly IInvocationRunner _invocationRunner;
    private readonly IGgufDownloadCoordinator _ggufDownloads;
    private readonly IImageModelDownloadCoordinator _imageModelDownloads;
    private readonly IWhisperModelDownloadCoordinator _whisperModelDownloads;
    private readonly ILlamaCppSourceBuildService _llamaCppSourceBuild;
    private readonly IWhisperCppSourceBuildService _whisperCppSourceBuild;
    private readonly IStableDiffusionCppSourceBuildService _stableDiffusionCppSourceBuild;
    private readonly IServiceScopeFactory _scopeFactory;

    public AppUpdateBusyProbe(IGpuWorkGate gpuWorkGate,
        IInvocationRunner invocationRunner,
        IGgufDownloadCoordinator ggufDownloads,
        IImageModelDownloadCoordinator imageModelDownloads,
        IWhisperModelDownloadCoordinator whisperModelDownloads,
        ILlamaCppSourceBuildService llamaCppSourceBuild,
        IWhisperCppSourceBuildService whisperCppSourceBuild,
        IStableDiffusionCppSourceBuildService stableDiffusionCppSourceBuild,
        IServiceScopeFactory scopeFactory)
    {
        _gpuWorkGate = gpuWorkGate ?? throw new ArgumentNullException(nameof(gpuWorkGate));
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _ggufDownloads = ggufDownloads ?? throw new ArgumentNullException(nameof(ggufDownloads));
        _imageModelDownloads = imageModelDownloads ?? throw new ArgumentNullException(nameof(imageModelDownloads));
        _whisperModelDownloads = whisperModelDownloads ?? throw new ArgumentNullException(nameof(whisperModelDownloads));
        _llamaCppSourceBuild = llamaCppSourceBuild ?? throw new ArgumentNullException(nameof(llamaCppSourceBuild));
        _whisperCppSourceBuild = whisperCppSourceBuild ?? throw new ArgumentNullException(nameof(whisperCppSourceBuild));
        _stableDiffusionCppSourceBuild = stableDiffusionCppSourceBuild ?? throw new ArgumentNullException(nameof(stableDiffusionCppSourceBuild));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>The work running right now; empty when the node is idle.</summary>
    public async Task<IReadOnlyList<AppUpdateBusyItem>> CollectAsync(CancellationToken ct)
    {
        var items = new List<AppUpdateBusyItem>();

        // Only the exclusive kinds: shared holders never surface through the gate's UX read.
        AppUpdateBusyKind? gpuKind = _gpuWorkGate.ExclusiveKind switch
        {
            GpuWorkKind.TrainingRun => AppUpdateBusyKind.TrainingRun,
            GpuWorkKind.EvaluationRun => AppUpdateBusyKind.EvaluationRun,
            GpuWorkKind.Export => AppUpdateBusyKind.TrainingExport,
            _ => null
        };
        if (gpuKind is { } kind)
        {
            items.Add(new AppUpdateBusyItem { Kind = kind });
        }

        items.AddRange(_ggufDownloads.ListStatuses()
                                     .Where(static status => status.Phase is GgufDownloadPhase.Running)
                                     .Select(static status => Named(AppUpdateBusyKind.ModelDownload, status.ModelName)));
        items.AddRange(_imageModelDownloads.ListStatuses()
                                           .Where(static status => status.Phase is ImageModelDownloadPhase.Running)
                                           .Select(static status => Named(AppUpdateBusyKind.ImageModelDownload, status.ModelName)));
        items.AddRange(_whisperModelDownloads.ListStatuses()
                                             .Where(static status => status.Phase is WhisperModelDownloadPhase.Running)
                                             .Select(static status => Named(AppUpdateBusyKind.TranscriptionModelDownload, status.ModelId)));

        if (_llamaCppSourceBuild.GetStatus().IsRunning)
        {
            items.Add(new AppUpdateBusyItem { Kind = AppUpdateBusyKind.LlamaCppSourceBuild });
        }

        if (_whisperCppSourceBuild.GetStatus().IsRunning)
        {
            items.Add(new AppUpdateBusyItem { Kind = AppUpdateBusyKind.WhisperCppSourceBuild });
        }

        if (_stableDiffusionCppSourceBuild.GetStatus().IsRunning)
        {
            items.Add(new AppUpdateBusyItem { Kind = AppUpdateBusyKind.StableDiffusionCppSourceBuild });
        }

        // Chat turns, agent runs, graph-workflow and integration calls all run through the invocation runner.
        if (_invocationRunner.ActiveInvocationCount > 0)
        {
            items.Add(new AppUpdateBusyItem { Kind = AppUpdateBusyKind.Invocation });
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sessions = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().ListAsync(ct);
        items.AddRange(sessions.Where(static session => session.Status is AgentWorkSessionStatus.Running)
                               .Select(static session => Named(AppUpdateBusyKind.WorkSession, session.Title)));

        return items;
    }

    private static AppUpdateBusyItem Named(AppUpdateBusyKind kind, string displayName) =>
        new()
        {
            Kind = kind,
            DisplayName = displayName
        };
}
