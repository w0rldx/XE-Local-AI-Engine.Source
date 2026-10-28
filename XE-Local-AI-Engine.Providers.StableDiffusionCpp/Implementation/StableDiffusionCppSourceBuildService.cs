namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Linux-only detached single-flight source build for stable-diffusion.cpp.</summary>
public sealed class StableDiffusionCppSourceBuildService
    : ManagedSourceBuildService<SdGpuBackend, StableDiffusionCppSourceBuildDescriptor>, IStableDiffusionCppSourceBuildService
{
    private readonly StableDiffusionCppRuntimeAdoption _adoption;
    private readonly IStableDiffusionCppSourceBuildPrerequisiteProbe _prerequisiteProbe;
    private readonly IStableDiffusionCppSourceBuildEventPublisher _publisher;

    public StableDiffusionCppSourceBuildService(IStableDiffusionCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IManagedRuntimeStore<SdGpuBackend> runtimeStore,
        IManagedSourceBuildSignal<SdGpuBackend> managedSignal,
        IImageRuntimeActivityGate activityGate,
        IStableDiffusionCppSourceBuildEventPublisher publisher,
        ILogger<StableDiffusionCppSourceBuildService> logger,
        TimeProvider timeProvider)
        : this(prerequisiteProbe,
            runtimeStore,
            managedSignal,
            activityGate,
            publisher,
            logger,
            RuntimeCacheDirectory.Resolve(),
            new SourceCommandRunner(),
            timeProvider)
    {
    }

    internal StableDiffusionCppSourceBuildService(IStableDiffusionCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IManagedRuntimeStore<SdGpuBackend> runtimeStore,
        IManagedSourceBuildSignal<SdGpuBackend> managedSignal,
        IImageRuntimeActivityGate activityGate,
        IStableDiffusionCppSourceBuildEventPublisher publisher,
        ILogger<StableDiffusionCppSourceBuildService> logger,
        string cacheRoot,
        ISourceCommandRunner runner,
        TimeProvider timeProvider,
        bool? isLinux = null)
        : base("stable-diffusion.cpp",
            "image runtime",
            "runtime",
            "sd-server",
            ["sd-server"],
            runtimeStore,
            managedSignal,
            activityGate,
            logger,
            cacheRoot,
            runner,
            timeProvider,
            isLinux)
    {
        _prerequisiteProbe = prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _adoption = new StableDiffusionCppRuntimeAdoption(cacheRoot, runtimeStore, managedSignal, timeProvider, logger);
    }

    protected override ManagedRuntimeAdoption<SdGpuBackend> Adoption => _adoption;

    public Task<StableDiffusionCppSourceBuildStartResult> StartAsync(StableDiffusionCppSourceBuildRequest request, CancellationToken ct)
    {
        return StartCoreAsync(async innerCt =>
            {
                var normalized = StableDiffusionCppSourceBuildRequestValidation.Normalize(request);
                await RecoverCoreAsync(innerCt).ConfigureAwait(false);
                var prerequisites = await _prerequisiteProbe.ProbeAsync(normalized.Backend, innerCt).ConfigureAwait(false);
                var revisionMode = ResolveRevisionMode(normalized.Source, normalized.Commit);
                var descriptor = new StableDiffusionCppSourceBuildDescriptor
                {
                    Backend = normalized.Backend,
                    Source = normalized.Source,
                    Repository = normalized.Repository!,
                    RevisionMode = revisionMode,
                    RequestedCommit = normalized.Commit,
                    ResolvedCommit = revisionMode == ManagedSourceRevisionMode.EnginePinned
                        ? StableDiffusionReleasePins.PinnedSourceCommitSha
                        : null,
                    BuildId = Guid.NewGuid()
                };
                return new StartPlan<StableDiffusionCppSourceBuildPrerequisiteReport>
                {
                    Prerequisites = prerequisites,
                    CanBuild = prerequisites.CanBuild,
                    InsufficientDisk = prerequisites.Items.Any(static item => item.Key == "free-disk" && !item.Satisfied),
                    Build = descriptor,
                    BuildId = descriptor.BuildId,
                    Repository = descriptor.Repository,
                    CommitToFetch = revisionMode == ManagedSourceRevisionMode.EnginePinned
                        ? StableDiffusionReleasePins.PinnedSourceCommitSha
                        : descriptor.RequestedCommit
                };
            },
            static (outcome, prerequisites, activity) => new StableDiffusionCppSourceBuildStartResult
            {
                Outcome = (StableDiffusionCppSourceBuildStartOutcome)outcome,
                Prerequisites = prerequisites,
                Activity = activity
            },
            ct);
    }

    public Task<StableDiffusionCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct)
    {
        return RemoveCoreAsync(static (outcome, activity) => new StableDiffusionCppSourceBuildRemoveResult
            {
                Outcome = (StableDiffusionCppSourceBuildRemoveOutcome)outcome,
                Activity = activity
            },
            ct);
    }

    public StableDiffusionCppSourceBuildStatus GetStatus()
    {
        var status = ReadStatus();
        return new StableDiffusionCppSourceBuildStatus
        {
            Phase = (StableDiffusionCppSourceBuildPhase)status.Phase,
            IsRunning = status.IsRunning,
            Terminal = status.Terminal,
            LogLines = status.LogLines,
            LogStartSequence = status.LogStartSequence,
            SanitizedError = status.SanitizedError,
            CurrentBuild = status.CurrentBuild,
            StartedAtUtc = status.StartedAtUtc,
            CompletedAtUtc = status.CompletedAtUtc
        };
    }

    internal static IReadOnlyList<string> BuildCMakeConfigureArguments(string sourceDir, string buildDir, SdGpuBackend backend)
    {
        var backendFlags = backend switch
        {
            SdGpuBackend.Cuda => new[]
            {
                "-DSD_CUDA=ON",
                "-DSD_VULKAN=OFF"
            },
            SdGpuBackend.Vulkan => ["-DSD_CUDA=OFF", "-DSD_VULKAN=ON"],
            _ => ["-DSD_CUDA=OFF", "-DSD_VULKAN=OFF"]
        };
        return ["-S", sourceDir, "-B", buildDir, "-DCMAKE_BUILD_TYPE=Release", .. backendFlags];
    }

    protected override Task<IReadOnlyList<string>> ConfigureArgumentsAsync(StableDiffusionCppSourceBuildDescriptor build,
        string sourceDir,
        string buildDir,
        CancellationToken ct)
    {
        return Task.FromResult(BuildCMakeConfigureArguments(sourceDir, buildDir, build.Backend));
    }

    protected override Task ValidateBuildAsync(StableDiffusionCppSourceBuildDescriptor build, string buildDir, string serverPath, CancellationToken ct)
    {
        ValidateRequestedBackendArtifacts(buildDir, build.Backend);
        return Task.CompletedTask;
    }

    protected override Task AdoptAsync(StableDiffusionCppSourceBuildDescriptor build, string buildDir, string serverPath, CancellationToken ct)
    {
        return _adoption.AdoptAsync(buildDir, serverPath, build, ct);
    }

    protected override StableDiffusionCppSourceBuildDescriptor WithResolvedCommit(StableDiffusionCppSourceBuildDescriptor build, string resolvedCommit)
    {
        return build with
        {
            ResolvedCommit = resolvedCommit
        };
    }

    protected override Task PublishStatusAsync(StatusChange change, CancellationToken ct)
    {
        return _publisher.PublishStatusAsync(new StableDiffusionCppSourceBuildStatusEvent
            {
                Phase = (StableDiffusionCppSourceBuildPhase)change.Phase,
                AppendedLogLines = change.AppendedLogLines,
                AppendedLogStartSequence = change.AppendedLogStartSequence,
                Terminal = change.Terminal,
                SanitizedError = change.SanitizedError,
                CurrentBuild = change.CurrentBuild
            },
            ct);
    }

    protected override string BackendDirectoryName(SdGpuBackend backend)
    {
        return BackendSlug(backend);
    }

    protected override Exception RuntimeError(string message)
    {
        return new StableDiffusionRuntimeException(message);
    }

    internal static void ValidateRequestedBackendArtifacts(string buildDir, SdGpuBackend backend)
    {
        var cachePath = Path.Combine(buildDir, "CMakeCache.txt");
        if (!File.Exists(cachePath))
        {
            throw new StableDiffusionRuntimeException("The source build did not produce a CMake backend manifest.");
        }

        var cacheLines = File.ReadLines(cachePath).ToHashSet(StringComparer.Ordinal);
        var expectedFlags = backend switch
        {
            SdGpuBackend.Cuda => new[]
            {
                "SD_CUDA:BOOL=ON",
                "SD_VULKAN:BOOL=OFF"
            },
            SdGpuBackend.Vulkan => ["SD_CUDA:BOOL=OFF", "SD_VULKAN:BOOL=ON"],
            _ => ["SD_CUDA:BOOL=OFF", "SD_VULKAN:BOOL=OFF"]
        };
        if (!expectedFlags.All(cacheLines.Contains))
        {
            throw new StableDiffusionRuntimeException($"The source build did not enable the requested {BackendSlug(backend)} backend.");
        }

        var backendArtifact = backend switch
        {
            SdGpuBackend.Cuda => "ggml-cuda",
            SdGpuBackend.Vulkan => "ggml-vulkan",
            _ => null
        };
        if (backendArtifact is not null
            && !Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories)
                         .Any(path => Path.GetFileName(path).Contains(backendArtifact, StringComparison.OrdinalIgnoreCase)))
        {
            throw new StableDiffusionRuntimeException($"The source build did not produce the requested {BackendSlug(backend)} backend artifact.");
        }
    }

    private static string BackendSlug(SdGpuBackend backend)
    {
        return backend switch
        {
            SdGpuBackend.Cuda => "cuda",
            SdGpuBackend.Vulkan => "vulkan",
            _ => "cpu"
        };
    }
}
