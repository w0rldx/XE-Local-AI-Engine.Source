namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Linux-only detached single-flight source build for stable-diffusion.cpp.</summary>
public sealed class StableDiffusionCppSourceBuildService : IStableDiffusionCppSourceBuildService, IDisposable
{
    private const int MaxBuildJobs = 8;
    private const int MaxLogLines = 500;
    private const int PublishQueueCapacity = 128;
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

    private readonly string _cacheRoot;
    private readonly IStableDiffusionManagedSourceBuildSignal _managedSignal;
    private readonly IStableDiffusionInstalledRuntimeStore _runtimeStore;
    private readonly IImageRuntimeActivityGate _activityGate;
    private readonly StableDiffusionCppRuntimeAdoption _adoption;
    private readonly ILogger<StableDiffusionCppSourceBuildService> _logger;
    private readonly IStableDiffusionCppSourceBuildEventPublisher _publisher;
    private readonly IStableDiffusionCppSourceBuildPrerequisiteProbe _prerequisiteProbe;
    private readonly Channel<StableDiffusionCppSourceBuildStatusEvent> _publishQueue;
    private readonly Task _publisherTask;
    private readonly IStableDiffusionSourceCommandRunner _runner;
    private readonly bool _isLinux;
    private readonly SemaphoreSlim _startGate = new(initialCount: 1, maxCount: 1);
    private readonly Lock _stateLock = new();
    private Task? _activeBuildTask;
    private CancellationTokenSource? _buildCts;
    private DateTimeOffset? _completedAtUtc;
    private StableDiffusionCppSourceBuildDescriptor? _currentBuild;
    private bool _isRunning;
    private bool _isStopping;
    private readonly List<string> _logLines = [];
    private long _logStartSequence;
    private long _nextLogSequence;
    private StableDiffusionCppSourceBuildPhase _phase;
    private string? _sanitizedError;
    private DateTimeOffset? _startedAtUtc;

    public StableDiffusionCppSourceBuildService(IStableDiffusionCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IStableDiffusionInstalledRuntimeStore runtimeStore,
        IStableDiffusionManagedSourceBuildSignal managedSignal,
        IImageRuntimeActivityGate activityGate,
        IStableDiffusionCppSourceBuildEventPublisher publisher,
        ILogger<StableDiffusionCppSourceBuildService> logger)
        : this(prerequisiteProbe, runtimeStore, managedSignal, activityGate, publisher, logger, DefaultCacheRoot(), new StableDiffusionSourceCommandRunner())
    {
    }

    internal StableDiffusionCppSourceBuildService(IStableDiffusionCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IStableDiffusionInstalledRuntimeStore runtimeStore,
        IStableDiffusionManagedSourceBuildSignal managedSignal,
        IImageRuntimeActivityGate activityGate,
        IStableDiffusionCppSourceBuildEventPublisher publisher,
        ILogger<StableDiffusionCppSourceBuildService> logger,
        string cacheRoot,
        IStableDiffusionSourceCommandRunner runner,
        bool? isLinux = null)
    {
        _prerequisiteProbe = prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));
        _runtimeStore = runtimeStore ?? throw new ArgumentNullException(nameof(runtimeStore));
        _managedSignal = managedSignal ?? throw new ArgumentNullException(nameof(managedSignal));
        _activityGate = activityGate ?? throw new ArgumentNullException(nameof(activityGate));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _publishQueue = Channel.CreateBounded<StableDiffusionCppSourceBuildStatusEvent>(new BoundedChannelOptions(PublishQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _publisherTask = PublishLoopAsync();
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _adoption = new StableDiffusionCppRuntimeAdoption(_cacheRoot, _runtimeStore, _managedSignal, _logger);
        _isLinux = isLinux ?? OperatingSystem.IsLinux();
    }

    private string BuildRoot => Path.Combine(_cacheRoot, "stable-diffusion.cpp", "source-build");
    private string MarkerPath => Path.Combine(WorkRoot, ".build-in-progress");
    private string RuntimeRoot => Path.Combine(_cacheRoot, "stable-diffusion.cpp", "managed");
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

    public async Task<StableDiffusionCppSourceBuildStartResult> StartAsync(StableDiffusionCppSourceBuildRequest request, CancellationToken ct)
    {
        if (!_isLinux)
        {
            throw new StableDiffusionRuntimeException("In-app source builds are available on Linux only.");
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        TaskCompletionSource? startSignal = null;
        try
        {
            lock (_stateLock)
            {
                if (_isStopping)
                {
                    throw new StableDiffusionRuntimeException("The image runtime source-build service is stopping.");
                }

                if (_isRunning)
                {
                    return new StableDiffusionCppSourceBuildStartResult(StableDiffusionCppSourceBuildStartOutcome.AlreadyRunning);
                }
            }

            var normalized = StableDiffusionCppSourceBuildRequestValidation.Normalize(request);
            await RecoverCoreAsync(ct).ConfigureAwait(false);
            var prerequisites = await _prerequisiteProbe.ProbeAsync(normalized.Backend, ct).ConfigureAwait(false);
            if (!prerequisites.CanBuild)
            {
                var outcome = prerequisites.Items.Any(static item => item.Key == "free-disk" && !item.Satisfied)
                    ? StableDiffusionCppSourceBuildStartOutcome.InsufficientDisk
                    : StableDiffusionCppSourceBuildStartOutcome.MissingPrerequisites;
                return new StableDiffusionCppSourceBuildStartResult(outcome, prerequisites);
            }

            var mutationReservation = _activityGate.TryAcquireMutationReservation();
            if (mutationReservation is null)
            {
                return new StableDiffusionCppSourceBuildStartResult(StableDiffusionCppSourceBuildStartOutcome.RuntimeBusy,
                    prerequisites,
                    _activityGate.GetSnapshot());
            }

            var revisionMode = normalized.Source == StableDiffusionCppSourceSelection.Official
                ? StableDiffusionCppSourceRevisionMode.EnginePinned
                : StableDiffusionCppSourceRevisionMode.ExplicitCommit;
            if (normalized.Source == StableDiffusionCppSourceSelection.Custom && normalized.Commit is null)
            {
                revisionMode = StableDiffusionCppSourceRevisionMode.DefaultBranch;
            }

            var descriptor = new StableDiffusionCppSourceBuildDescriptor(normalized.Backend,
                normalized.Source,
                normalized.Repository!,
                revisionMode,
                normalized.Commit,
                revisionMode == StableDiffusionCppSourceRevisionMode.EnginePinned
                    ? StableDiffusionReleasePins.PinnedSourceCommitSha
                    : null)
            {
                BuildId = Guid.NewGuid()
            };

            try
            {
                var buildCts = new CancellationTokenSource();
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
                    _phase = StableDiffusionCppSourceBuildPhase.Cloning;
                    _logLines.Clear();
                    _logStartSequence = 0;
                    _nextLogSequence = 0;
                    _sanitizedError = null;
                    _currentBuild = descriptor;
                    _startedAtUtc = DateTimeOffset.UtcNow;
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
        return new StableDiffusionCppSourceBuildStartResult(StableDiffusionCppSourceBuildStartOutcome.Started);
    }

    public async Task<StableDiffusionCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return new StableDiffusionCppSourceBuildRemoveResult(StableDiffusionCppSourceBuildRemoveOutcome.RuntimeBusy,
                        _activityGate.GetSnapshot());
                }
            }

            using var mutation = _activityGate.TryAcquireMutationReservation();
            if (mutation is null)
            {
                return new StableDiffusionCppSourceBuildRemoveResult(StableDiffusionCppSourceBuildRemoveOutcome.RuntimeBusy,
                    _activityGate.GetSnapshot());
            }

            var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
            if (installed is null)
            {
                return new StableDiffusionCppSourceBuildRemoveResult(StableDiffusionCppSourceBuildRemoveOutcome.NotInstalled);
            }

            SetPhase(StableDiffusionCppSourceBuildPhase.Removing);
            DeleteManagedRuntime(installed);
            await _runtimeStore.DeleteAsync(ct).ConfigureAwait(false);
            _managedSignal.Clear();
            SetTerminal(StableDiffusionCppSourceBuildPhase.Completed, error: null);
            return new StableDiffusionCppSourceBuildRemoveResult(StableDiffusionCppSourceBuildRemoveOutcome.Removed);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public StableDiffusionCppSourceBuildStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new StableDiffusionCppSourceBuildStatus(_phase,
                _isRunning,
                _phase is StableDiffusionCppSourceBuildPhase.Completed
                    or StableDiffusionCppSourceBuildPhase.Cancelled
                    or StableDiffusionCppSourceBuildPhase.Failed,
                [.. _logLines],
                _logStartSequence,
                _sanitizedError,
                _currentBuild,
                _startedAtUtc,
                _completedAtUtc);
        }
    }

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

    private async Task<BuildCompletion> RunBuildAsync(StableDiffusionCppSourceBuildDescriptor descriptor, CancellationToken ct)
    {
        try
        {
            PrepareWorkTree();
            await File.WriteAllTextAsync(MarkerPath, descriptor.BuildId.ToString("D"), ct).ConfigureAwait(false);
            var sourceDir = Path.Combine(WorkRoot, "source");
            var buildDir = Path.Combine(WorkRoot, "build");

            SetPhase(StableDiffusionCppSourceBuildPhase.Cloning);
            var requestedCommit = descriptor.RevisionMode == StableDiffusionCppSourceRevisionMode.EnginePinned
                ? StableDiffusionReleasePins.PinnedSourceCommitSha
                : descriptor.RequestedCommit;
            await RunRequiredAsync("git", GitArguments("init", sourceDir), WorkRoot, ShortCommandTimeout, captureOutput: false, ct)
                .ConfigureAwait(false);
            await RunRequiredAsync("git", GitArguments("remote", "add", "origin", descriptor.Repository), sourceDir, ShortCommandTimeout, captureOutput: false, ct)
                .ConfigureAwait(false);
            await RunRequiredAsync("git",
                    GitArguments("fetch", "--depth=1", "--no-tags", "--no-recurse-submodules", "origin", requestedCommit ?? "HEAD"),
                    sourceDir,
                    CloneTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);

            SetPhase(StableDiffusionCppSourceBuildPhase.Verifying);
            await RunRequiredAsync("git", GitArguments("checkout", "--detach", "FETCH_HEAD"), sourceDir, ShortCommandTimeout, captureOutput: false, ct)
                .ConfigureAwait(false);
            var resolvedCommit = (await RunRequiredAsync("git",
                    GitArguments("rev-parse", "HEAD"),
                    sourceDir,
                    ShortCommandTimeout,
                    captureOutput: true,
                    ct)
                .ConfigureAwait(false)).StandardOutput.Trim();
            if (resolvedCommit.Length != 40 || !resolvedCommit.All(Uri.IsHexDigit)
                                            || requestedCommit is not null && !string.Equals(resolvedCommit, requestedCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new StableDiffusionRuntimeException("The source checkout did not resolve to the requested exact commit.");
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

            SetPhase(StableDiffusionCppSourceBuildPhase.Configuring);
            var cmakeArgs = BuildCMakeConfigureArguments(sourceDir, buildDir, descriptor.Backend);
            await RunRequiredAsync("cmake", cmakeArgs, WorkRoot, ConfigureTimeout, captureOutput: false, ct).ConfigureAwait(false);

            SetPhase(StableDiffusionCppSourceBuildPhase.Building);
            var buildJobs = Math.Max(1, Math.Min(Environment.ProcessorCount, MaxBuildJobs));
            await RunRequiredAsync("cmake",
                ["--build", buildDir, "--target", "sd-server", "--config", "Release", "--parallel", buildJobs.ToString()],
                WorkRoot,
                BuildTimeout,
                captureOutput: false,
                ct).ConfigureAwait(false);

            var serverPath = FindServer(buildDir)
                             ?? throw new StableDiffusionRuntimeException("The stable-diffusion.cpp build did not produce sd-server.");
            EnsureExecutable(serverPath);
            ValidateRequestedBackendArtifacts(buildDir, descriptor.Backend);

            SetPhase(StableDiffusionCppSourceBuildPhase.SmokeTesting);
            await RunRequiredAsync(serverPath,
                    ["--help"],
                    Path.GetDirectoryName(serverPath)!,
                    SmokeTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);

            SetPhase(StableDiffusionCppSourceBuildPhase.Adopting);
            await _adoption.AdoptAsync(buildDir, serverPath, descriptor, ct).ConfigureAwait(false);
            return new BuildCompletion(StableDiffusionCppSourceBuildPhase.Completed, Error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BuildCompletion(StableDiffusionCppSourceBuildPhase.Cancelled, Error: null);
        }
        catch (TimeoutException exception)
        {
            _logger.LogWarning(exception, "stable-diffusion.cpp source build timed out.");
            return new BuildCompletion(StableDiffusionCppSourceBuildPhase.Failed,
                "A stable-diffusion.cpp source-build command timed out. Review the sanitized build log.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "stable-diffusion.cpp source build failed.");
            return new BuildCompletion(StableDiffusionCppSourceBuildPhase.Failed,
                "The stable-diffusion.cpp source build failed. Review the sanitized build log.");
        }
        finally
        {
            TryDeleteDirectory(WorkRoot);
        }
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

    private async Task<StableDiffusionSourceCommandResult> RunRequiredAsync(string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct)
    {
        AppendLog($"> {Path.GetFileName(fileName)} {string.Join(' ', arguments)}");
        var result = await _runner.RunAsync(fileName,
            arguments,
            workingDirectory,
            line => AppendLog(SanitizeLogLine(line)),
            timeout,
            captureOutput,
            ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new StableDiffusionRuntimeException($"The source-build command '{Path.GetFileName(fileName)}' failed.");
        }

        return result;
    }

    private void AppendLog(string line)
    {
        StableDiffusionCppSourceBuildStatusEvent statusEvent;
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

    private void SetPhase(StableDiffusionCppSourceBuildPhase phase)
    {
        StableDiffusionCppSourceBuildStatusEvent statusEvent;
        lock (_stateLock)
        {
            _phase = phase;
            statusEvent = CreateEventUnderLock([], _nextLogSequence);
        }

        QueuePublish(statusEvent);
    }

    private void SetTerminal(StableDiffusionCppSourceBuildPhase phase, string? error)
    {
        StableDiffusionCppSourceBuildStatusEvent statusEvent;
        lock (_stateLock)
        {
            _phase = phase;
            _sanitizedError = error;
            _completedAtUtc = DateTimeOffset.UtcNow;
            statusEvent = CreateEventUnderLock([], _nextLogSequence);
        }

        QueuePublish(statusEvent);
    }

    private void CompleteBuild(BuildCompletion completion)
    {
        StableDiffusionCppSourceBuildStatusEvent statusEvent;
        lock (_stateLock)
        {
            _phase = completion.Phase;
            _isRunning = false;
            _sanitizedError = completion.Error;
            _completedAtUtc = DateTimeOffset.UtcNow;
            _buildCts?.Dispose();
            _buildCts = null;
            statusEvent = CreateEventUnderLock([], _nextLogSequence);
        }

        QueuePublish(statusEvent);
    }

    private StableDiffusionCppSourceBuildStatusEvent CreateEventUnderLock(IReadOnlyList<string> appended, long startSequence)
    {
        return new StableDiffusionCppSourceBuildStatusEvent(_phase,
            appended,
            startSequence,
            _phase is StableDiffusionCppSourceBuildPhase.Completed
                or StableDiffusionCppSourceBuildPhase.Cancelled
                or StableDiffusionCppSourceBuildPhase.Failed,
            _sanitizedError,
            _currentBuild);
    }

    private void QueuePublish(StableDiffusionCppSourceBuildStatusEvent statusEvent)
    {
        if (!_publishQueue.Writer.TryWrite(statusEvent))
        {
            _logger.LogDebug("Dropped a stable-diffusion.cpp source-build status event because the publisher is stopping.");
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
                _logger.LogWarning(exception, "Failed to publish stable-diffusion.cpp source-build status.");
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
                _phase = StableDiffusionCppSourceBuildPhase.Failed;
                _sanitizedError = "A previously interrupted source build was recovered and its temporary files were removed.";
                _completedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (installed?.Validity == StableDiffusionInstalledRuntimeValidity.Active)
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
        StableDiffusionCppRuntimeAdoption.CreateOwnerOnlyDirectory(BuildRoot);
        StableDiffusionCppRuntimeAdoption.CreateOwnerOnlyDirectory(WorkRoot);
    }

    private void DeleteManagedRuntime(StableDiffusionInstalledRuntimeState installed)
    {
        var installRoot = GetManagedInstallRoot(installed);
        if (Directory.Exists(installRoot))
        {
            Directory.Delete(installRoot, recursive: true);
        }

        if (Directory.Exists(installRoot))
        {
            throw new StableDiffusionRuntimeException("The managed runtime could not be removed.");
        }
    }

    private string GetManagedInstallRoot(StableDiffusionInstalledRuntimeState installed)
    {
        if (installed.SourceCommit.Length != 40 || !installed.SourceCommit.All(Uri.IsHexDigit))
        {
            throw new StableDiffusionRuntimeException("The recorded managed runtime commit is invalid.");
        }

        var root = Path.GetFullPath(RuntimeRoot);
        var installRoot = Path.GetFullPath(Path.Combine(root, BackendSlug(installed.DesiredBackend), installed.SourceCommit));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!installRoot.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new StableDiffusionRuntimeException("The recorded managed runtime path is outside the managed cache.");
        }

        return installRoot;
    }

    private static string? FindServer(string buildDir)
    {
        return Directory.Exists(buildDir)
            ? Directory.EnumerateFiles(buildDir, "sd-server", SearchOption.AllDirectories).FirstOrDefault()
            : null;
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

    private static string BackendSlug(SdGpuBackend backend)
    {
        return backend switch
        {
            SdGpuBackend.Cuda => "cuda",
            SdGpuBackend.Vulkan => "vulkan",
            _ => "cpu"
        };
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

    private sealed record BuildCompletion(StableDiffusionCppSourceBuildPhase Phase, string? Error);
}
