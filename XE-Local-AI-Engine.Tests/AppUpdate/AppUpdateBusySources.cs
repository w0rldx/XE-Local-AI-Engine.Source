namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>The busy sources an <see cref="AppUpdateBusyProbe" /> reads, idle until a test makes one of them busy.</summary>
internal sealed class AppUpdateBusySources
{
    public AppUpdateBusySources()
    {
        GgufDownloads.ListStatuses().Returns([]);
        ImageModelDownloads.ListStatuses().Returns([]);
        WhisperModelDownloads.ListStatuses().Returns([]);
        SetLlamaCppSourceBuildRunning(false);
        WhisperCppSourceBuild.GetStatus().Returns(new WhisperCppSourceBuildStatus
        {
            Phase = WhisperCppSourceBuildPhase.Idle,
            IsRunning = false,
            Terminal = false,
            LogLines = [],
            LogStartSequence = 0,
            SanitizedError = null,
            CurrentBuild = null,
            StartedAtUtc = null,
            CompletedAtUtc = null
        });
        StableDiffusionCppSourceBuild.GetStatus().Returns(new StableDiffusionCppSourceBuildStatus
        {
            Phase = StableDiffusionCppSourceBuildPhase.Idle,
            IsRunning = false,
            Terminal = false,
            LogLines = [],
            LogStartSequence = 0,
            SanitizedError = null,
            CurrentBuild = null,
            StartedAtUtc = null,
            CompletedAtUtc = null
        });
        WorkSessions.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    public IGpuWorkGate GpuWorkGate { get; } = Substitute.For<IGpuWorkGate>();

    public IInvocationRunner InvocationRunner { get; } = Substitute.For<IInvocationRunner>();

    public IGgufDownloadCoordinator GgufDownloads { get; } = Substitute.For<IGgufDownloadCoordinator>();

    public IImageModelDownloadCoordinator ImageModelDownloads { get; } = Substitute.For<IImageModelDownloadCoordinator>();

    public IWhisperModelDownloadCoordinator WhisperModelDownloads { get; } = Substitute.For<IWhisperModelDownloadCoordinator>();

    public ILlamaCppSourceBuildService LlamaCppSourceBuild { get; } = Substitute.For<ILlamaCppSourceBuildService>();

    public IWhisperCppSourceBuildService WhisperCppSourceBuild { get; } = Substitute.For<IWhisperCppSourceBuildService>();

    public IStableDiffusionCppSourceBuildService StableDiffusionCppSourceBuild { get; } = Substitute.For<IStableDiffusionCppSourceBuildService>();

    public IWorkSessionService WorkSessions { get; } = Substitute.For<IWorkSessionService>();

    /// <summary>A training run on the GPU, a running GGUF download and a running work session.</summary>
    public AppUpdateBusySources WithTrainingDownloadAndSession()
    {
        GpuWorkGate.ExclusiveKind.Returns(GpuWorkKind.TrainingRun);
        GgufDownloads.ListStatuses().Returns([
            new GgufDownloadStatus
            {
                ModelName = "qwen3-8b.gguf",
                Phase = GgufDownloadPhase.Running,
                CompletedBytes = 1,
                TotalBytes = 10,
                SanitizedError = null
            },
            new GgufDownloadStatus
            {
                ModelName = "finished.gguf",
                Phase = GgufDownloadPhase.Completed,
                CompletedBytes = 10,
                TotalBytes = 10,
                SanitizedError = null
            }
        ]);
        WorkSessions.ListAsync(Arg.Any<CancellationToken>()).Returns([
            Session("Refactor the parser", AgentWorkSessionStatus.Running),
            Session("Old research", AgentWorkSessionStatus.Completed)
        ]);
        return this;
    }

    public void SetLlamaCppSourceBuildRunning(bool isRunning) =>
        LlamaCppSourceBuild.GetStatus().Returns(new LlamaCppSourceBuildStatus
        {
            Phase = isRunning ? LlamaCppSourceBuildPhase.Cloning : LlamaCppSourceBuildPhase.Idle,
            IsRunning = isRunning,
            Terminal = false,
            LogLines = [],
            LogStartSequence = 0,
            SanitizedError = null,
            CurrentBuild = null,
            StartedAtUtc = null,
            CompletedAtUtc = null
        });

    public AppUpdateBusyProbe Probe()
    {
        // The real container stands in for the host's scope factory; only the scoped work-session service is read.
        var services = new ServiceCollection().AddScoped(_ => WorkSessions).BuildServiceProvider();
        return new AppUpdateBusyProbe(GpuWorkGate,
            InvocationRunner,
            GgufDownloads,
            ImageModelDownloads,
            WhisperModelDownloads,
            LlamaCppSourceBuild,
            WhisperCppSourceBuild,
            StableDiffusionCppSourceBuild,
            services.GetRequiredService<IServiceScopeFactory>());
    }

    private static WorkSessionSummary Session(string title, AgentWorkSessionStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Kind = AgentWorkSessionKind.General,
            Status = status,
            AgentDefinitionId = Guid.NewGuid(),
            StepCount = 1,
            UpdatedUtc = 0
        };
}
