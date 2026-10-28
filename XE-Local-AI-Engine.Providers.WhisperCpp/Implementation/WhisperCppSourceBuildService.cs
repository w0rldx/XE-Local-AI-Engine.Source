namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Linux-only, detached, single-flight source build for a managed whisper.cpp runtime.
/// </summary>
/// <remarks>
///     A start attempted while anything holds the runtime is refused with <see cref="WhisperCppSourceBuildStartOutcome.RuntimeBusy" />.
///     The <c>readelf</c> relocation gate runs through <see cref="ISourceCommandRunner" /> like every other command, so tests drive it
///     without compiling.
/// </remarks>
public sealed partial class WhisperCppSourceBuildService
    : ManagedSourceBuildService<WhisperBackend, WhisperCppSourceBuildDescriptor>, IWhisperCppSourceBuildService
{
    private readonly WhisperCppRuntimeAdoption _adoption;
    private readonly IWhisperCppSourceBuildPrerequisiteProbe _prerequisiteProbe;
    private readonly IWhisperCppSourceBuildEventPublisher _publisher;

    public WhisperCppSourceBuildService(IWhisperCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IManagedRuntimeStore<WhisperBackend> runtimeStore,
        IManagedSourceBuildSignal<WhisperBackend> managedSignal,
        IWhisperRuntimeActivityGate activityGate,
        IWhisperCppSourceBuildEventPublisher publisher,
        ILogger<WhisperCppSourceBuildService> logger,
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

    internal WhisperCppSourceBuildService(IWhisperCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IManagedRuntimeStore<WhisperBackend> runtimeStore,
        IManagedSourceBuildSignal<WhisperBackend> managedSignal,
        IWhisperRuntimeActivityGate activityGate,
        IWhisperCppSourceBuildEventPublisher publisher,
        ILogger<WhisperCppSourceBuildService> logger,
        string cacheRoot,
        ISourceCommandRunner runner,
        TimeProvider timeProvider,
        bool? isLinux = null)
        : base("whisper.cpp",
            "transcription runtime",
            "whisper.cpp runtime",
            "whisper-server",
            ["whisper-server", "whisper-cli"],
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
        _adoption = new WhisperCppRuntimeAdoption(cacheRoot, runtimeStore, managedSignal, timeProvider, logger);
    }

    protected override ManagedRuntimeAdoption<WhisperBackend> Adoption => _adoption;

    /// <inheritdoc />
    public Task<WhisperCppSourceBuildStartResult> StartAsync(WhisperCppSourceBuildRequest request, CancellationToken ct)
    {
        return StartCoreAsync(async innerCt =>
            {
                var normalized = WhisperCppSourceBuildRequestValidation.Normalize(request);
                await RecoverCoreAsync(innerCt).ConfigureAwait(false);
                var prerequisites = await _prerequisiteProbe.ProbeAsync(normalized.Backend, innerCt).ConfigureAwait(false);
                var revisionMode = ResolveRevisionMode(normalized.Source, normalized.Commit);
                var descriptor = new WhisperCppSourceBuildDescriptor
                {
                    Backend = normalized.Backend,
                    Source = normalized.Source,
                    Repository = normalized.Repository!,
                    RevisionMode = revisionMode,
                    RequestedCommit = normalized.Commit,
                    ResolvedCommit = revisionMode == ManagedSourceRevisionMode.EnginePinned
                        ? WhisperCppReleasePins.PinnedSourceCommitSha
                        : null,
                    BuildId = Guid.NewGuid()
                };
                return new StartPlan<WhisperCppSourceBuildPrerequisiteReport>
                {
                    Prerequisites = prerequisites,
                    CanBuild = prerequisites.CanBuild,
                    InsufficientDisk = prerequisites.Items.Any(static item => item.Key == "free-disk" && !item.Satisfied),
                    Build = descriptor,
                    BuildId = descriptor.BuildId,
                    Repository = descriptor.Repository,
                    CommitToFetch = revisionMode == ManagedSourceRevisionMode.EnginePinned
                        ? WhisperCppReleasePins.PinnedSourceCommitSha
                        : descriptor.RequestedCommit
                };
            },
            static (outcome, prerequisites, activity) => new WhisperCppSourceBuildStartResult
            {
                Outcome = (WhisperCppSourceBuildStartOutcome)outcome,
                Prerequisites = prerequisites,
                Activity = activity
            },
            ct);
    }

    /// <inheritdoc />
    public Task<WhisperCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct)
    {
        return RemoveCoreAsync(static (outcome, activity) => new WhisperCppSourceBuildRemoveResult
            {
                Outcome = (WhisperCppSourceBuildRemoveOutcome)outcome,
                Activity = activity
            },
            ct);
    }

    /// <inheritdoc />
    public WhisperCppSourceBuildStatus GetStatus()
    {
        var status = ReadStatus();
        return new WhisperCppSourceBuildStatus
        {
            Phase = (WhisperCppSourceBuildPhase)status.Phase,
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

    /// <summary>
    ///     The cmake configure line. The three rpath arguments are load-bearing and are asserted by name in tests.
    /// </summary>
    /// <remarks>
    ///     A stock build writes an ABSOLUTE build-tree <c>RUNPATH</c> into the binary. Adoption then moves <c>build/bin</c> out of the
    ///     work root into the managed install root, so that path points at a directory that no longer exists and the relocated
    ///     <c>whisper-server</c> dies at startup unable to open <c>libwhisper.so.1</c>, even though the library sits right beside it. The
    ///     <c>$ORIGIN</c> install rpath is passed as a bare literal: there is no shell in this path, so escaping it would write the
    ///     escaping into the binary.
    /// </remarks>
    internal static IReadOnlyList<string> BuildCMakeConfigureArguments(string sourceDir,
        string buildDir,
        WhisperBackend backend,
        string? cudaArchitectures = null)
    {
        List<string> arguments =
        [
            "-S", sourceDir,
            "-B", buildDir,
            "-DCMAKE_BUILD_TYPE=Release",
            "-DCMAKE_BUILD_RPATH_USE_ORIGIN=ON",
            "-DCMAKE_BUILD_WITH_INSTALL_RPATH=ON",
            "-DCMAKE_INSTALL_RPATH=$ORIGIN",
            $"-DGGML_CUDA={(backend == WhisperBackend.Cuda ? "ON" : "OFF")}"
        ];
        if (backend == WhisperBackend.Cuda)
        {
            arguments.Add($"-DCMAKE_CUDA_ARCHITECTURES={cudaArchitectures ?? SourceBuildPolicy.DefaultCudaArchitectures}");
        }

        return arguments;
    }

    /// <summary>
    ///     Asserts that the produced binary can survive being moved: a present <c>RUNPATH</c>/<c>RPATH</c> whose every
    ///     element is <c>$ORIGIN</c>-relative.
    /// </summary>
    /// <remarks>
    ///     Checked on the bytes rather than on the cmake arguments that were supposed to produce them, and run BEFORE
    ///     adoption so a runtime that would die at first spawn never becomes this node's managed runtime.
    /// </remarks>
    internal static void ValidateRelocatableRunpath(string readelfOutput)
    {
        var match = RunpathRegex().Match(readelfOutput ?? string.Empty);
        if (!match.Success)
        {
            throw new WhisperRuntimeException("The source build produced a runtime that cannot be relocated.");
        }

        var entries = match.Groups["value"].Value
                           .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0 || !entries.All(static entry => entry.StartsWith("$ORIGIN", StringComparison.Ordinal)))
        {
            throw new WhisperRuntimeException("The source build produced a runtime that cannot be relocated.");
        }
    }

    internal static void ValidateRequestedBackendArtifacts(string buildDir, WhisperBackend backend)
    {
        var cachePath = Path.Combine(buildDir, "CMakeCache.txt");
        if (!File.Exists(cachePath))
        {
            throw new WhisperRuntimeException("The source build did not produce a CMake backend manifest.");
        }

        var cacheLines = File.ReadLines(cachePath).ToHashSet(StringComparer.Ordinal);
        var expectedFlag = backend == WhisperBackend.Cuda ? "GGML_CUDA:BOOL=ON" : "GGML_CUDA:BOOL=OFF";
        if (!cacheLines.Contains(expectedFlag))
        {
            throw new WhisperRuntimeException($"The source build did not enable the requested {BackendSlug(backend)} backend.");
        }

        // The cache says what was asked for; a named artifact is what proves the toolchain actually produced it.
        if (backend == WhisperBackend.Cuda
            && !Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories)
                         .Any(static path => Path.GetFileName(path).Contains("ggml-cuda", StringComparison.OrdinalIgnoreCase)))
        {
            throw new WhisperRuntimeException("The source build did not produce the requested cuda backend artifact.");
        }
    }

    protected override async Task<IReadOnlyList<string>> ConfigureArgumentsAsync(WhisperCppSourceBuildDescriptor build,
        string sourceDir,
        string buildDir,
        CancellationToken ct)
    {
        var architectures = build.Backend == WhisperBackend.Cuda
            ? await ResolveCudaArchitecturesAsync(ct).ConfigureAwait(false)
            : null;
        return BuildCMakeConfigureArguments(sourceDir, buildDir, build.Backend, architectures);
    }

    protected override async Task ValidateBuildAsync(WhisperCppSourceBuildDescriptor build, string buildDir, string serverPath, CancellationToken ct)
    {
        await ValidateServerRunpathAsync(serverPath, ct).ConfigureAwait(false);
        ValidateRequestedBackendArtifacts(buildDir, build.Backend);
    }

    protected override Task AdoptAsync(WhisperCppSourceBuildDescriptor build, string buildDir, string serverPath, CancellationToken ct)
    {
        return _adoption.AdoptAsync(buildDir, serverPath, build, ct);
    }

    protected override WhisperCppSourceBuildDescriptor WithResolvedCommit(WhisperCppSourceBuildDescriptor build, string resolvedCommit)
    {
        return build with
        {
            ResolvedCommit = resolvedCommit
        };
    }

    protected override Task PublishStatusAsync(StatusChange change, CancellationToken ct)
    {
        return _publisher.PublishStatusAsync(new WhisperCppSourceBuildStatusEvent
            {
                Phase = (WhisperCppSourceBuildPhase)change.Phase,
                AppendedLogLines = change.AppendedLogLines,
                AppendedLogStartSequence = change.AppendedLogStartSequence,
                Terminal = change.Terminal,
                SanitizedError = change.SanitizedError,
                CurrentBuild = change.CurrentBuild
            },
            ct);
    }

    protected override string BackendDirectoryName(WhisperBackend backend)
    {
        return BackendSlug(backend);
    }

    protected override Exception RuntimeError(string message)
    {
        return new WhisperRuntimeException(message);
    }

    private async Task ValidateServerRunpathAsync(string serverPath, CancellationToken ct)
    {
        var result = await RunRequiredAsync("readelf",
                ["-d", serverPath],
                Path.GetDirectoryName(serverPath)!,
                SourceBuildPolicy.ShortCommandTimeout,
                captureOutput: true,
                ct)
            .ConfigureAwait(false);
        ValidateRelocatableRunpath(result.StandardOutput);
    }

    private async Task<string> ResolveCudaArchitecturesAsync(CancellationToken ct)
    {
        try
        {
            var result = await Runner.RunAsync("nvidia-smi",
                                         ["--query-gpu=compute_cap", "--format=csv,noheader"],
                                         WorkRoot,
                                         _ => { },
                                         SourceBuildPolicy.ShortCommandTimeout,
                                         captureOutput: true,
                                         ct)
                                     .ConfigureAwait(false);
            return result.ExitCode == 0 ? SourceBuildPolicy.ParseCudaArchitectures(result.StandardOutput) : SourceBuildPolicy.DefaultCudaArchitectures;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or InvalidOperationException)
        {
            // A driver probe that cannot answer is not a reason to fail a build: the conservative set still compiles.
            return SourceBuildPolicy.DefaultCudaArchitectures;
        }
    }

    private static string BackendSlug(WhisperBackend backend)
    {
        return backend == WhisperBackend.Cuda ? "cuda" : "cpu";
    }

    [GeneratedRegex(@"\((?:RUNPATH|RPATH)\)[^\[]*\[(?<value>[^\]]*)\]", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex RunpathRegex();
}
