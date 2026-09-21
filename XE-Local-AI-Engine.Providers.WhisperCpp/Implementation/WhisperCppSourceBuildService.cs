namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Linux-only, detached, single-flight source build for a managed whisper.cpp runtime.
/// </summary>
/// <remarks>
///     The build runs on its own task and reports through <see cref="GetStatus" />, because it takes minutes and the operator's request
///     must not hold a connection open for it. It holds the activity gate's mutation reservation for its whole duration, so an eject or
///     a transcription cannot race the tree it is about to replace, and a start attempted while anything holds the runtime is refused
///     with <see cref="WhisperCppSourceBuildStartOutcome.RuntimeBusy" /> instead. Every command goes through
///     <see cref="IWhisperSourceCommandRunner" />, the <c>readelf</c> relocation gate included, so tests drive it without compiling.
/// </remarks>
public sealed partial class WhisperCppSourceBuildService : IWhisperCppSourceBuildService, IDisposable
{
    private const int MaxBuildJobs = 8;
    private const int MaxLogLines = 500;
    private const int PublishQueueCapacity = 128;

    // Conservative fallback set when nvidia-smi's compute_cap cannot be read or validated, matching the llama.cpp
    // managed build's choice so two managed lanes on one box do not disagree about what they target.
    private const string DefaultCudaArchitectures = "75;86;89;120";

    private static readonly TimeSpan BuildTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(20);

    private static readonly string[] GitHardeningArguments =
    [
        "-c", "protocol.allow=never",
        "-c", "protocol.https.allow=always",
        "-c", "credential.helper=",
        "-c", "core.askPass="
    ];

    private readonly IWhisperRuntimeActivityGate _activityGate;
    private readonly WhisperCppRuntimeAdoption _adoption;
    private readonly string _cacheRoot;
    private readonly bool _isLinux;
    private readonly ILogger<WhisperCppSourceBuildService> _logger;
    private readonly IWhisperManagedSourceBuildSignal _managedSignal;
    private readonly IWhisperCppSourceBuildEventPublisher _publisher;
    private readonly Channel<WhisperCppSourceBuildStatusEvent> _publishQueue;
    private readonly Task _publisherTask;
    private readonly IWhisperCppSourceBuildPrerequisiteProbe _prerequisiteProbe;
    private readonly IWhisperSourceCommandRunner _runner;
    private readonly IWhisperInstalledRuntimeStore _runtimeStore;
    private readonly SemaphoreSlim _startGate = new(initialCount: 1, maxCount: 1);
    private readonly Lock _stateLock = new();
    private Task? _activeBuildTask;
    private CancellationTokenSource? _buildCts;
    private DateTimeOffset? _completedAtUtc;
    private WhisperCppSourceBuildDescriptor? _currentBuild;
    private bool _isRunning;
    private bool _isStopping;
    private readonly List<string> _logLines = [];
    private long _logStartSequence;
    private long _nextLogSequence;
    private WhisperCppSourceBuildPhase _phase;
    private string? _sanitizedError;
    private DateTimeOffset? _startedAtUtc;
    private readonly TimeProvider _timeProvider;

    public WhisperCppSourceBuildService(IWhisperCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IWhisperInstalledRuntimeStore runtimeStore,
        IWhisperManagedSourceBuildSignal managedSignal,
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
            DefaultCacheRoot(),
            new WhisperSourceCommandRunner(),
            timeProvider)
    {
    }

    internal WhisperCppSourceBuildService(IWhisperCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IWhisperInstalledRuntimeStore runtimeStore,
        IWhisperManagedSourceBuildSignal managedSignal,
        IWhisperRuntimeActivityGate activityGate,
        IWhisperCppSourceBuildEventPublisher publisher,
        ILogger<WhisperCppSourceBuildService> logger,
        string cacheRoot,
        IWhisperSourceCommandRunner runner,
        TimeProvider timeProvider,
        bool? isLinux = null)
    {
        _prerequisiteProbe = prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));
        _runtimeStore = runtimeStore ?? throw new ArgumentNullException(nameof(runtimeStore));
        _managedSignal = managedSignal ?? throw new ArgumentNullException(nameof(managedSignal));
        _activityGate = activityGate ?? throw new ArgumentNullException(nameof(activityGate));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _publishQueue = Channel.CreateBounded<WhisperCppSourceBuildStatusEvent>(new BoundedChannelOptions(PublishQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,

            // A slow subscriber must never stall the build, and the newest state is the one worth keeping.
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _publisherTask = PublishLoopAsync();
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _adoption = new WhisperCppRuntimeAdoption(_cacheRoot, _runtimeStore, _managedSignal, _timeProvider, _logger);
        _isLinux = isLinux ?? OperatingSystem.IsLinux();
    }

    private string BuildRoot => Path.Combine(_cacheRoot, "whisper.cpp", "source-build");
    private string MarkerPath => Path.Combine(WorkRoot, ".build-in-progress");
    private string RuntimeRoot => Path.Combine(_cacheRoot, "whisper.cpp", "managed");
    private string WorkRoot => Path.Combine(BuildRoot, ".work");

    public void Dispose()
    {
        try
        {
            _buildCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Build completion may dispose concurrently with host teardown.
        }

        _buildCts?.Dispose();
        _publishQueue.Writer.TryComplete();
        _startGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<WhisperCppSourceBuildStartResult> StartAsync(WhisperCppSourceBuildRequest request, CancellationToken ct)
    {
        if (!_isLinux)
        {
            throw new WhisperRuntimeException("In-app source builds are available on Linux only.");
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        TaskCompletionSource? startSignal = null;
        try
        {
            lock (_stateLock)
            {
                if (_isStopping)
                {
                    throw new WhisperRuntimeException("The transcription runtime source-build service is stopping.");
                }

                if (_isRunning)
                {
                    return new WhisperCppSourceBuildStartResult { Outcome = WhisperCppSourceBuildStartOutcome.AlreadyRunning };
                }
            }

            var normalized = WhisperCppSourceBuildRequestValidation.Normalize(request);
            await RecoverCoreAsync(ct).ConfigureAwait(false);
            var prerequisites = await _prerequisiteProbe.ProbeAsync(normalized.Backend, ct).ConfigureAwait(false);
            if (!prerequisites.CanBuild)
            {
                // Disk is separated from the rest so the endpoint can say "not enough space" rather than send the
                // operator hunting a missing compiler.
                var outcome = prerequisites.Items.Any(static item => item.Key == "free-disk" && !item.Satisfied)
                    ? WhisperCppSourceBuildStartOutcome.InsufficientDisk
                    : WhisperCppSourceBuildStartOutcome.MissingPrerequisites;
                return new WhisperCppSourceBuildStartResult { Outcome = outcome, Prerequisites = prerequisites };
            }

            var mutationReservation = _activityGate.TryAcquireMutationReservation();
            if (mutationReservation is null)
            {
                return new WhisperCppSourceBuildStartResult
                {
                    Outcome = WhisperCppSourceBuildStartOutcome.RuntimeBusy,
                    Prerequisites = prerequisites,
                    Activity = _activityGate.GetSnapshot()
                };
            }

            var revisionMode = normalized.Source == WhisperCppSourceSelection.Official
                ? WhisperCppSourceRevisionMode.EnginePinned
                : WhisperCppSourceRevisionMode.ExplicitCommit;
            if (normalized.Source == WhisperCppSourceSelection.Custom && normalized.Commit is null)
            {
                revisionMode = WhisperCppSourceRevisionMode.DefaultBranch;
            }

            var descriptor = new WhisperCppSourceBuildDescriptor
            {
                Backend = normalized.Backend,
                Source = normalized.Source,
                Repository = normalized.Repository!,
                RevisionMode = revisionMode,
                RequestedCommit = normalized.Commit,
                ResolvedCommit = revisionMode == WhisperCppSourceRevisionMode.EnginePinned
                    ? WhisperCppReleasePins.PinnedSourceCommitSha
                    : null,
                BuildId = Guid.NewGuid()
            };

            try
            {
                var buildCts = new CancellationTokenSource();

                // The detached task waits on this signal so it cannot start — and cannot complete — before the state below describes it; otherwise a fast failure could publish a terminal phase that
                // the state write then overwrites with "running".
                startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Task.Run(async () =>
                {
                    await startSignal.Task.ConfigureAwait(false);
                    BuildCompletion completion;
                    try
                    {
                        completion = await RunBuildAsync(descriptor, buildCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        mutationReservation.Dispose();
                    }

                    CompleteBuild(completion);
                }, CancellationToken.None);

                lock (_stateLock)
                {
                    _isRunning = true;
                    _phase = WhisperCppSourceBuildPhase.Cloning;
                    _logLines.Clear();
                    _logStartSequence = 0;
                    _nextLogSequence = 0;
                    _sanitizedError = null;
                    _currentBuild = descriptor;
                    _startedAtUtc = _timeProvider.GetUtcNow();
                    _completedAtUtc = null;
                    _buildCts?.Dispose();
                    _buildCts = buildCts;
                    _activeBuildTask = task;
                }
            }
            catch
            {
                mutationReservation.Dispose();
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }

        startSignal.SetResult();
        return new WhisperCppSourceBuildStartResult { Outcome = WhisperCppSourceBuildStartOutcome.Started };
    }

    /// <inheritdoc />
    public async Task<WhisperCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return new WhisperCppSourceBuildRemoveResult
                    {
                        Outcome = WhisperCppSourceBuildRemoveOutcome.RuntimeBusy,
                        Activity = _activityGate.GetSnapshot()
                    };
                }
            }

            await using var mutation = _activityGate.TryAcquireMutationReservation();
            if (mutation is null)
            {
                return new WhisperCppSourceBuildRemoveResult
                {
                    Outcome = WhisperCppSourceBuildRemoveOutcome.RuntimeBusy,
                    Activity = _activityGate.GetSnapshot()
                };
            }

            var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
            if (installed is null)
            {
                return new WhisperCppSourceBuildRemoveResult { Outcome = WhisperCppSourceBuildRemoveOutcome.NotInstalled };
            }

            SetPhase(WhisperCppSourceBuildPhase.Removing);
            DeleteManagedRuntime(installed);
            await _runtimeStore.DeleteAsync(ct).ConfigureAwait(false);
            _managedSignal.Clear();
            SetTerminal(WhisperCppSourceBuildPhase.Completed, error: null);
            return new WhisperCppSourceBuildRemoveResult { Outcome = WhisperCppSourceBuildRemoveOutcome.Removed };
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <inheritdoc />
    public WhisperCppSourceBuildStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new WhisperCppSourceBuildStatus
            {
                Phase = _phase,
                IsRunning = _isRunning,
                Terminal = _phase is WhisperCppSourceBuildPhase.Completed
                    or WhisperCppSourceBuildPhase.Cancelled
                    or WhisperCppSourceBuildPhase.Failed,
                LogLines = [.. _logLines],
                LogStartSequence = _logStartSequence,
                SanitizedError = _sanitizedError,
                CurrentBuild = _currentBuild,
                StartedAtUtc = _startedAtUtc,
                CompletedAtUtc = _completedAtUtc
            };
        }
    }

    /// <inheritdoc />
    public bool Cancel()
    {
        lock (_stateLock)
        {
            if (!_isRunning || _buildCts is null)
            {
                return false;
            }

            _buildCts.Cancel();
            return true;
        }
    }

    /// <inheritdoc />
    public async Task RecoverAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken ct)
    {
        Task? task;
        CancellationTokenSource? buildCts;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                _isStopping = true;
                buildCts = _buildCts;
                task = _activeBuildTask;
            }
        }
        finally
        {
            _startGate.Release();
        }

        if (buildCts is not null)
        {
            try
            {
                await buildCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Build completion won the race; the captured task below is still authoritative.
            }
        }

        if (task is not null)
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
        }

        _publishQueue.Writer.TryComplete();
        await _publisherTask.WaitAsync(ct).ConfigureAwait(false);
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
            arguments.Add($"-DCMAKE_CUDA_ARCHITECTURES={cudaArchitectures ?? DefaultCudaArchitectures}");
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

    internal static string ParseCudaArchitectures(string output)
    {
        var values = new SortedSet<int>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = ComputeCapabilityRegex().Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["major"].Value, out var major)
                || !int.TryParse(match.Groups["minor"].Value, out var minor))
            {
                return DefaultCudaArchitectures;
            }

            var architecture = (major * 10) + minor;
            if (!IsSupportedCudaArchitecture(architecture))
            {
                return DefaultCudaArchitectures;
            }

            _ = values.Add(architecture);
        }

        return values.Count == 0 ? DefaultCudaArchitectures : string.Join(';', values);
    }

    private async Task<BuildCompletion> RunBuildAsync(WhisperCppSourceBuildDescriptor descriptor, CancellationToken ct)
    {
        try
        {
            PrepareWorkTree();
            await File.WriteAllTextAsync(MarkerPath, descriptor.BuildId.ToString("D"), ct).ConfigureAwait(false);
            var sourceDir = Path.Combine(WorkRoot, "source");
            var buildDir = Path.Combine(WorkRoot, "build");

            SetPhase(WhisperCppSourceBuildPhase.Cloning);
            var requestedCommit = descriptor.RevisionMode == WhisperCppSourceRevisionMode.EnginePinned
                ? WhisperCppReleasePins.PinnedSourceCommitSha
                : descriptor.RequestedCommit;
            await RunRequiredAsync("git", GitArguments("init", sourceDir), WorkRoot, ShortCommandTimeout, captureOutput: false, ct)
                .ConfigureAwait(false);
            await RunRequiredAsync("git",
                    GitArguments("remote", "add", "origin", descriptor.Repository),
                    sourceDir,
                    ShortCommandTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);
            await RunRequiredAsync("git",
                    GitArguments("fetch", "--depth=1", "--no-tags", "--no-recurse-submodules", "origin", requestedCommit ?? "HEAD"),
                    sourceDir,
                    CloneTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);

            SetPhase(WhisperCppSourceBuildPhase.Verifying);
            await RunRequiredAsync("git",
                    GitArguments("checkout", "--detach", "FETCH_HEAD"),
                    sourceDir,
                    ShortCommandTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);
            var resolvedCommit = (await RunRequiredAsync("git",
                    GitArguments("rev-parse", "HEAD"),
                    sourceDir,
                    ShortCommandTimeout,
                    captureOutput: true,
                    ct)
                .ConfigureAwait(false)).StandardOutput.Trim();

            // The engine-pinned revision is a PEELED commit SHA, never a tag object: fetching a tag object's SHA has nothing to land on. Asserting the checkout resolved to exactly what was asked for
            // is what catches a pin that silently drifted.
            if (resolvedCommit.Length != 40
                || !resolvedCommit.All(Uri.IsHexDigit)
                || (requestedCommit is not null && !string.Equals(resolvedCommit, requestedCommit, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WhisperRuntimeException("The source checkout did not resolve to the requested exact commit.");
            }

            await RunRequiredAsync("git",
                    GitArguments("submodule", "update", "--init", "--recursive"),
                    sourceDir,
                    CloneTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);

            descriptor = descriptor with
            {
                ResolvedCommit = Convert.ToHexStringLower(Convert.FromHexString(resolvedCommit))
            };
            lock (_stateLock)
            {
                _currentBuild = descriptor;
            }

            SetPhase(WhisperCppSourceBuildPhase.Configuring);
            var architectures = descriptor.Backend == WhisperBackend.Cuda
                ? await ResolveCudaArchitecturesAsync(ct).ConfigureAwait(false)
                : null;
            var cmakeArgs = BuildCMakeConfigureArguments(sourceDir, buildDir, descriptor.Backend, architectures);
            await RunRequiredAsync("cmake", cmakeArgs, WorkRoot, ConfigureTimeout, captureOutput: false, ct).ConfigureAwait(false);

            SetPhase(WhisperCppSourceBuildPhase.Building);
            var buildJobs = Math.Max(1, Math.Min(Environment.ProcessorCount, MaxBuildJobs));
            await RunRequiredAsync("cmake",
                    [
                        "--build", buildDir,
                        "--target", "whisper-server", "whisper-cli",
                        "--config", "Release",
                        "--parallel", buildJobs.ToString(CultureInfo.InvariantCulture)
                    ],
                    WorkRoot,
                    BuildTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);

            var serverPath = FindServer(buildDir)
                             ?? throw new WhisperRuntimeException("The whisper.cpp build did not produce whisper-server.");
            EnsureExecutable(serverPath);
            await ValidateServerRunpathAsync(serverPath, ct).ConfigureAwait(false);
            ValidateRequestedBackendArtifacts(buildDir, descriptor.Backend);

            SetPhase(WhisperCppSourceBuildPhase.SmokeTesting);
            await RunRequiredAsync(serverPath, ["--help"], Path.GetDirectoryName(serverPath)!, SmokeTimeout, captureOutput: false, ct)
                .ConfigureAwait(false);

            SetPhase(WhisperCppSourceBuildPhase.Adopting);
            await _adoption.AdoptAsync(buildDir, serverPath, descriptor, ct).ConfigureAwait(false);
            return new BuildCompletion { Phase = WhisperCppSourceBuildPhase.Completed, Error = null };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BuildCompletion { Phase = WhisperCppSourceBuildPhase.Cancelled, Error = null };
        }
        catch (TimeoutException exception)
        {
            _logger.LogWarning(exception, "whisper.cpp source build timed out.");
            return new BuildCompletion
            {
                Phase = WhisperCppSourceBuildPhase.Failed,
                Error = "A whisper.cpp source-build command timed out. Review the sanitized build log."
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "whisper.cpp source build failed.");
            return new BuildCompletion
            {
                Phase = WhisperCppSourceBuildPhase.Failed,
                Error = "The whisper.cpp source build failed. Review the sanitized build log."
            };
        }
        finally
        {
            TryDeleteDirectory(WorkRoot);
        }
    }

    private async Task ValidateServerRunpathAsync(string serverPath, CancellationToken ct)
    {
        var result = await RunRequiredAsync("readelf",
                ["-d", serverPath],
                Path.GetDirectoryName(serverPath)!,
                ShortCommandTimeout,
                captureOutput: true,
                ct)
            .ConfigureAwait(false);
        ValidateRelocatableRunpath(result.StandardOutput);
    }

    private async Task<string> ResolveCudaArchitecturesAsync(CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync("nvidia-smi",
                                          ["--query-gpu=compute_cap", "--format=csv,noheader"],
                                          WorkRoot,
                                          _ => { },
                                          ShortCommandTimeout,
                                          captureOutput: true,
                                          ct)
                                      .ConfigureAwait(false);
            return result.ExitCode == 0 ? ParseCudaArchitectures(result.StandardOutput) : DefaultCudaArchitectures;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or InvalidOperationException)
        {
            // A driver probe that cannot answer is not a reason to fail a build: the conservative set still compiles.
            return DefaultCudaArchitectures;
        }
    }

    private static bool IsSupportedCudaArchitecture(int architecture)
    {
        return architecture is 50 or 52 or 53
            or 60 or 61 or 62
            or 70 or 72 or 75
            or 80 or 86 or 87 or 89
            or 90
            or 100 or 101 or 103
            or 110
            or 120 or 121;
    }

    private async Task<WhisperSourceCommandResult> RunRequiredAsync(string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct)
    {
        // Sanitized like every child output line: cmake's -S/-B arguments are absolute paths under the cache root,
        // which in production sits inside the operator's home directory.
        AppendLog(SanitizeLogLine($"> {Path.GetFileName(fileName)} {string.Join(' ', arguments)}"));
        var result = await _runner.RunAsync(fileName,
                                      arguments,
                                      workingDirectory,
                                      line => AppendLog(SanitizeLogLine(line)),
                                      timeout,
                                      captureOutput,
                                      ct)
                                  .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WhisperRuntimeException($"The source-build command '{Path.GetFileName(fileName)}' failed.");
        }

        return result;
    }

    private void AppendLog(string line)
    {
        WhisperCppSourceBuildStatusEvent statusEvent;
        lock (_stateLock)
        {
            _logLines.Add(line);
            _nextLogSequence++;
            if (_logLines.Count > MaxLogLines)
            {
                _logLines.RemoveAt(0);
                _logStartSequence++;
            }

            statusEvent = CreateEventUnderLock([line], _nextLogSequence - 1);
        }

        QueuePublish(statusEvent);
    }

    private void SetPhase(WhisperCppSourceBuildPhase phase)
    {
        WhisperCppSourceBuildStatusEvent statusEvent;
        lock (_stateLock)
        {
            _phase = phase;
            statusEvent = CreateEventUnderLock([], _nextLogSequence);
        }

        QueuePublish(statusEvent);
    }

    private void SetTerminal(WhisperCppSourceBuildPhase phase, string? error)
    {
        WhisperCppSourceBuildStatusEvent statusEvent;
        lock (_stateLock)
        {
            _phase = phase;
            _sanitizedError = error;
            _completedAtUtc = _timeProvider.GetUtcNow();
            statusEvent = CreateEventUnderLock([], _nextLogSequence);
        }

        QueuePublish(statusEvent);
    }

    private void CompleteBuild(BuildCompletion completion)
    {
        WhisperCppSourceBuildStatusEvent statusEvent;
        lock (_stateLock)
        {
            _phase = completion.Phase;
            _isRunning = false;
            _sanitizedError = completion.Error;
            _completedAtUtc = _timeProvider.GetUtcNow();
            _buildCts?.Dispose();
            _buildCts = null;
            statusEvent = CreateEventUnderLock([], _nextLogSequence);
        }

        QueuePublish(statusEvent);
    }

    private WhisperCppSourceBuildStatusEvent CreateEventUnderLock(IReadOnlyList<string> appended, long startSequence)
    {
        return new WhisperCppSourceBuildStatusEvent
        {
            Phase = _phase,
            AppendedLogLines = appended,
            AppendedLogStartSequence = startSequence,
            Terminal = _phase is WhisperCppSourceBuildPhase.Completed
                or WhisperCppSourceBuildPhase.Cancelled
                or WhisperCppSourceBuildPhase.Failed,
            SanitizedError = _sanitizedError,
            CurrentBuild = _currentBuild
        };
    }

    private void QueuePublish(WhisperCppSourceBuildStatusEvent statusEvent)
    {
        if (!_publishQueue.Writer.TryWrite(statusEvent))
        {
            _logger.LogDebug("Dropped a whisper.cpp source-build status event because the publisher is stopping.");
        }
    }

    private async Task PublishLoopAsync()
    {
        await foreach (var statusEvent in _publishQueue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await _publisher.PublishStatusAsync(statusEvent, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to publish whisper.cpp source-build status.");
            }
        }
    }

    private async Task RecoverCoreAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_isRunning)
            {
                return;
            }
        }

        await _adoption.RecoverAsync(ct).ConfigureAwait(false);

        if (File.Exists(MarkerPath) || Directory.Exists(WorkRoot))
        {
            TryDeleteDirectory(WorkRoot);
            lock (_stateLock)
            {
                _phase = WhisperCppSourceBuildPhase.Failed;
                _sanitizedError = "A previously interrupted source build was recovered and its temporary files were removed.";
                _completedAtUtc = _timeProvider.GetUtcNow();
            }
        }

        // Republishing the signal is the whole reason this runs at host start: the backend selector only trusts a managed runtime once something has set it, so without this an adopted CUDA build
        // stops resolving after a restart and the node silently falls back.
        var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (installed?.Validity == WhisperInstalledRuntimeValidity.Active)
        {
            _managedSignal.SetActive(installed.DesiredBackend);
        }
        else
        {
            _managedSignal.Clear();
        }
    }

    private void PrepareWorkTree()
    {
        TryDeleteDirectory(WorkRoot);
        WhisperCppRuntimeAdoption.CreateOwnerOnlyDirectory(BuildRoot);
        WhisperCppRuntimeAdoption.CreateOwnerOnlyDirectory(WorkRoot);
    }

    private void DeleteManagedRuntime(WhisperInstalledRuntimeState installed)
    {
        var installRoot = GetManagedInstallRoot(installed);
        if (Directory.Exists(installRoot))
        {
            Directory.Delete(installRoot, recursive: true);
        }

        if (Directory.Exists(installRoot))
        {
            throw new WhisperRuntimeException("The managed whisper.cpp runtime could not be removed.");
        }
    }

    // Derived from the record's own commit and backend rather than from its recorded path, so a tampered or
    // tombstoned record can never point a recursive delete outside the managed cache.
    private string GetManagedInstallRoot(WhisperInstalledRuntimeState installed)
    {
        if (installed.SourceCommit.Length != 40 || !installed.SourceCommit.All(Uri.IsHexDigit))
        {
            throw new WhisperRuntimeException("The recorded managed whisper.cpp runtime commit is invalid.");
        }

        var root = Path.GetFullPath(RuntimeRoot);
        var installRoot = Path.GetFullPath(Path.Combine(root, BackendSlug(installed.DesiredBackend), installed.SourceCommit));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!installRoot.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new WhisperRuntimeException("The recorded managed whisper.cpp runtime path is outside the managed cache.");
        }

        return installRoot;
    }

    private static string? FindServer(string buildDir)
    {
        return Directory.Exists(buildDir)
            ? Directory.EnumerateFiles(buildDir, "whisper-server", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
    }

    private static string SanitizeLogLine(string line)
    {
        var value = line.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~", StringComparison.Ordinal);
        return value.Length <= 1000 ? value : value[..1000];
    }

    private static string BackendSlug(WhisperBackend backend)
    {
        return backend == WhisperBackend.Cuda ? "cuda" : "cpu";
    }

    private static IReadOnlyList<string> GitArguments(params string[] arguments)
    {
        return [.. GitHardeningArguments, .. arguments];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the caller reports the primary operation.
        }
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine");
    }

    [GeneratedRegex(@"\((?:RUNPATH|RPATH)\)[^\[]*\[(?<value>[^\]]*)\]", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex RunpathRegex();

    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex ComputeCapabilityRegex();

    private sealed record BuildCompletion
    {
        public required WhisperCppSourceBuildPhase Phase { get; init; }

        public required string? Error { get; init; }
    }
}
