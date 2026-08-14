namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
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
        _isLinux = isLinux ?? OperatingSystem.IsLinux();
    }

    private string BuildRoot => Path.Combine(_cacheRoot, "stable-diffusion.cpp", "source-build");
    private string AdoptionJournalPath => Path.Combine(BuildRoot, "adoption-journal.json");
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
            await AdoptBuildAsync(buildDir, serverPath, descriptor, ct).ConfigureAwait(false);
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

        await RecoverAdoptionAsync(ct).ConfigureAwait(false);

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

    private async Task RecoverAdoptionAsync(CancellationToken ct)
    {
        if (!File.Exists(AdoptionJournalPath))
        {
            return;
        }

        StableDiffusionCppAdoptionJournal journal;
        try
        {
            await using var stream = File.OpenRead(AdoptionJournalPath);
            journal = await JsonSerializer.DeserializeAsync<StableDiffusionCppAdoptionJournal>(stream, cancellationToken: ct).ConfigureAwait(false)
                      ?? throw new StableDiffusionRuntimeException("The managed image runtime adoption journal is invalid.");
        }
        catch (JsonException exception)
        {
            throw new StableDiffusionRuntimeException("The managed image runtime adoption journal is invalid.", exception);
        }

        var paths = GetAdoptionPaths(journal);
        var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
        var committed = installed is not null
                        && RuntimeStatesMatch(installed, journal.NewState)
                        && Directory.Exists(paths.Destination);
        if (committed)
        {
            try
            {
                CleanupCommittedAdoption(paths);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "A committed stable-diffusion.cpp runtime cleanup remains pending and will be retried.");
            }

            return;
        }

        if (journal.HadPreviousDestination
            && !Directory.Exists(paths.Backup)
            && Directory.Exists(paths.Destination)
            && journal.PreviousState is not null
            && installed is not null
            && RuntimeStatesMatch(installed, journal.PreviousState))
        {
            if (await ManagedRuntimeBytesMatchStateAsync(journal.PreviousState, paths.Destination, ct).ConfigureAwait(false))
            {
                DeleteDirectoryStrict(paths.Failed);
                DeleteFileStrict(AdoptionJournalPath);
                return;
            }

            throw new StableDiffusionRuntimeException("The previous managed image runtime backup is missing and the installed bytes cannot be safely identified.");
        }

        await RollbackAdoptionAsync(journal, paths).ConfigureAwait(false);
    }

    private void PrepareWorkTree()
    {
        TryDeleteDirectory(WorkRoot);
        CreateOwnerOnlyDirectory(BuildRoot);
        CreateOwnerOnlyDirectory(WorkRoot);
    }

    private async Task AdoptBuildAsync(string buildDir,
        string serverPath,
        StableDiffusionCppSourceBuildDescriptor descriptor,
        CancellationToken ct)
    {
        var relativeServer = Path.GetRelativePath(buildDir, serverPath);
        if (relativeServer.StartsWith("..", StringComparison.Ordinal))
        {
            throw new StableDiffusionRuntimeException("The built sd-server path escaped the build directory.");
        }

        var backendRoot = Path.Combine(RuntimeRoot, BackendSlug(descriptor.Backend));
        var destination = Path.Combine(backendRoot, descriptor.ResolvedCommit!);
        var staging = Path.Combine(backendRoot, $".staging-{descriptor.ResolvedCommit}-{descriptor.BuildId:N}");
        var backup = Path.Combine(backendRoot, $".backup-{descriptor.ResolvedCommit}-{descriptor.BuildId:N}");
        var failed = Path.Combine(backendRoot, $".failed-{descriptor.ResolvedCommit}-{descriptor.BuildId:N}");
        CreateOwnerOnlyDirectory(RuntimeRoot);
        CreateOwnerOnlyDirectory(backendRoot);
        TryDeleteDirectory(staging);
        TryDeleteDirectory(backup);
        TryDeleteDirectory(failed);
        Directory.Move(buildDir, staging);
        try
        {
            HardenManagedTree(staging);
            var stagedServer = Path.GetFullPath(Path.Combine(staging, relativeServer));
            ValidateAdoptedServer(staging, stagedServer);
            var digest = await ComputeSha256Async(stagedServer, ct).ConfigureAwait(false);
            var finalServer = Path.GetFullPath(Path.Combine(destination, relativeServer));
            var state = new StableDiffusionInstalledRuntimeState(StableDiffusionInstalledRuntimeValidity.Active,
                descriptor.Backend,
                descriptor.Repository,
                descriptor.ResolvedCommit!,
                descriptor.Source,
                descriptor.RevisionMode,
                descriptor.RequestedCommit,
                Path.GetDirectoryName(finalServer),
                digest,
                DateTimeOffset.UtcNow);
            var previousState = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
            var hadPreviousDestination = Directory.Exists(destination);
            var journal = new StableDiffusionCppAdoptionJournal(descriptor.BuildId,
                descriptor.Backend,
                descriptor.ResolvedCommit!,
                hadPreviousDestination,
                previousState,
                state);
            var paths = GetAdoptionPaths(journal);
            await WriteAdoptionJournalAsync(journal, ct).ConfigureAwait(false);

            try
            {
                if (hadPreviousDestination)
                {
                    Directory.Move(destination, backup);
                }

                Directory.Move(staging, destination);
                ValidateAdoptedServer(destination, finalServer);
                await _runtimeStore.WriteAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception adoptionException)
            {
                try
                {
                    await RollbackAdoptionAsync(journal, paths).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    _managedSignal.Clear();
                    throw new StableDiffusionRuntimeException("The managed image runtime adoption failed and its previous state could not be restored.",
                        new AggregateException(adoptionException, rollbackException));
                }

                ExceptionDispatchInfo.Capture(adoptionException).Throw();
                throw;
            }

            _managedSignal.SetActive(descriptor.Backend);
            try
            {
                CleanupCommittedAdoption(paths);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "The previous stable-diffusion.cpp runtime cleanup is pending and will be retried.");
            }
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private async Task RollbackAdoptionAsync(StableDiffusionCppAdoptionJournal journal, AdoptionPaths paths)
    {
        _managedSignal.Clear();
        if (Directory.Exists(paths.Destination))
        {
            if (Directory.Exists(paths.Failed))
            {
                DeleteDirectoryStrict(paths.Failed);
            }

            Directory.Move(paths.Destination, paths.Failed);
        }

        if (journal.HadPreviousDestination)
        {
            if (!Directory.Exists(paths.Backup))
            {
                throw new StableDiffusionRuntimeException("The previous managed image runtime backup is missing.");
            }

            Directory.Move(paths.Backup, paths.Destination);
        }

        if (paths.RetiredPrevious is not null && Directory.Exists(paths.RetiredPrevious))
        {
            if (paths.PreviousInstallRoot is null || Directory.Exists(paths.PreviousInstallRoot))
            {
                throw new StableDiffusionRuntimeException("The previous managed image runtime could not be recovered.");
            }

            Directory.Move(paths.RetiredPrevious, paths.PreviousInstallRoot);
        }

        await RestorePreviousStateAsync(journal.PreviousState).ConfigureAwait(false);
        DeleteDirectoryStrict(paths.Failed);
        DeleteFileStrict(AdoptionJournalPath);
    }

    private async Task WriteAdoptionJournalAsync(StableDiffusionCppAdoptionJournal journal, CancellationToken ct)
    {
        CreateOwnerOnlyDirectory(BuildRoot);
        var temporaryPath = AdoptionJournalPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, journal, cancellationToken: ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(temporaryPath, AdoptionJournalPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(AdoptionJournalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private AdoptionPaths GetAdoptionPaths(StableDiffusionCppAdoptionJournal journal)
    {
        if (journal.NewCommit.Length != 40
            || !journal.NewCommit.All(Uri.IsHexDigit)
            || journal.NewState.SourceCommit != journal.NewCommit
            || journal.NewState.DesiredBackend != journal.NewBackend)
        {
            throw new StableDiffusionRuntimeException("The managed image runtime adoption journal is invalid.");
        }

        var backendRoot = Path.Combine(RuntimeRoot, BackendSlug(journal.NewBackend));
        var destination = Path.Combine(backendRoot, journal.NewCommit);
        var backup = Path.Combine(backendRoot, $".backup-{journal.NewCommit}-{journal.BuildId:N}");
        var failed = Path.Combine(backendRoot, $".failed-{journal.NewCommit}-{journal.BuildId:N}");
        string? previousInstallRoot = null;
        string? retiredPrevious = null;
        if (journal.PreviousState is not null)
        {
            previousInstallRoot = GetManagedInstallRoot(journal.PreviousState);
            if (!PathsEqual(previousInstallRoot, destination))
            {
                retiredPrevious = Path.Combine(Path.GetDirectoryName(previousInstallRoot)!,
                    $".retired-{journal.PreviousState.SourceCommit}-{journal.BuildId:N}");
            }
        }

        return new AdoptionPaths(destination, backup, failed, previousInstallRoot, retiredPrevious);
    }

    private void CleanupCommittedAdoption(AdoptionPaths paths)
    {
        if (paths.RetiredPrevious is not null && paths.PreviousInstallRoot is not null)
        {
            if (Directory.Exists(paths.PreviousInstallRoot) && !Directory.Exists(paths.RetiredPrevious))
            {
                Directory.Move(paths.PreviousInstallRoot, paths.RetiredPrevious);
            }

            DeleteDirectoryStrict(paths.RetiredPrevious);
        }

        DeleteDirectoryStrict(paths.Backup);
        DeleteDirectoryStrict(paths.Failed);
        DeleteFileStrict(AdoptionJournalPath);
    }

    private static bool RuntimeStatesMatch(StableDiffusionInstalledRuntimeState actual,
        StableDiffusionInstalledRuntimeState expected)
    {
        return actual.Validity == StableDiffusionInstalledRuntimeValidity.Active
               && actual.DesiredBackend == expected.DesiredBackend
               && string.Equals(actual.SourceRepository, expected.SourceRepository, StringComparison.Ordinal)
               && string.Equals(actual.SourceCommit, expected.SourceCommit, StringComparison.Ordinal)
               && actual.SourceSelection == expected.SourceSelection
               && actual.SourceRevisionMode == expected.SourceRevisionMode
               && string.Equals(actual.SourceRequestedCommit, expected.SourceRequestedCommit, StringComparison.Ordinal)
               && string.Equals(actual.SourceBuildPath, expected.SourceBuildPath, StringComparison.Ordinal)
               && string.Equals(actual.ServerSha256, expected.ServerSha256, StringComparison.Ordinal);
    }

    private static async Task<bool> ManagedRuntimeBytesMatchStateAsync(StableDiffusionInstalledRuntimeState state,
        string installRoot,
        CancellationToken ct)
    {
        if (state.SourceBuildPath is not { Length: > 0 } buildPath
            || state.ServerSha256 is not { Length: 64 } expectedSha
            || !expectedSha.All(Uri.IsHexDigit))
        {
            return false;
        }

        try
        {
            var fullInstallRoot = Path.GetFullPath(installRoot);
            var fullBuildPath = Path.GetFullPath(buildPath);
            var installPrefix = fullInstallRoot + Path.DirectorySeparatorChar;
            if (!string.Equals(fullBuildPath, fullInstallRoot, StringComparison.Ordinal)
                && !fullBuildPath.StartsWith(installPrefix,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return false;
            }

            var serverPath = Path.Combine(fullBuildPath, OperatingSystem.IsWindows() ? "sd-server.exe" : "sd-server");
            if (!File.Exists(serverPath) || new FileInfo(serverPath).LinkTarget is not null)
            {
                return false;
            }

            var actualSha = await ComputeSha256Async(serverPath, ct).ConfigureAwait(false);
            return string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    private async Task RestorePreviousStateAsync(StableDiffusionInstalledRuntimeState? previousState)
    {
        if (previousState is null)
        {
            await _runtimeStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
            _managedSignal.Clear();
            return;
        }

        await _runtimeStore.WriteAsync(previousState, CancellationToken.None).ConfigureAwait(false);
        if (previousState.Validity == StableDiffusionInstalledRuntimeValidity.Active)
        {
            _managedSignal.SetActive(previousState.DesiredBackend);
        }
        else
        {
            _managedSignal.Clear();
        }
    }

    private void ValidateAdoptedServer(string installRoot, string serverPath)
    {
        var cacheRoot = Path.GetFullPath(_cacheRoot);
        var fullInstallRoot = Path.GetFullPath(installRoot);
        var fullServerPath = Path.GetFullPath(serverPath);
        var installPrefix = fullInstallRoot + Path.DirectorySeparatorChar;
        var cachePrefix = cacheRoot + Path.DirectorySeparatorChar;
        if (!fullInstallRoot.StartsWith(cachePrefix, StringComparison.Ordinal)
            || !fullServerPath.StartsWith(installPrefix, StringComparison.Ordinal)
            || !File.Exists(fullServerPath)
            || new FileInfo(fullServerPath).LinkTarget is not null)
        {
            throw new StableDiffusionRuntimeException("The built sd-server failed managed-path validation.");
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var serverMode = File.GetUnixFileMode(fullServerPath);
        if ((serverMode & UnixFileMode.OtherWrite) != UnixFileMode.None
            || (serverMode & UnixFileMode.UserExecute) == UnixFileMode.None)
        {
            throw new StableDiffusionRuntimeException("The built sd-server has insecure permissions.");
        }

        var directory = Path.GetDirectoryName(fullServerPath);
        while (!string.IsNullOrEmpty(directory) && directory.Length >= cacheRoot.Length)
        {
            if (new DirectoryInfo(directory).LinkTarget is not null
                || (File.GetUnixFileMode(directory) & UnixFileMode.OtherWrite) != UnixFileMode.None)
            {
                throw new StableDiffusionRuntimeException("The built sd-server path chain is insecure.");
            }

            if (string.Equals(directory, cacheRoot, StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
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

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
    }

    private static void HardenManagedTree(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var existing = File.GetUnixFileMode(file);
            var execute = existing & UnixFileMode.UserExecute;
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | execute);
        }
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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

    private static void DeleteDirectoryStrict(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        if (Directory.Exists(path))
        {
            throw new IOException("A managed image runtime directory could not be removed.");
        }
    }

    private static void DeleteFileStrict(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (File.Exists(path))
        {
            throw new IOException("The managed image runtime adoption journal could not be removed.");
        }
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine");
    }

    private sealed record BuildCompletion(StableDiffusionCppSourceBuildPhase Phase, string? Error);

    private sealed record AdoptionPaths(
        string Destination,
        string Backup,
        string Failed,
        string? PreviousInstallRoot,
        string? RetiredPrevious);
}

internal sealed record StableDiffusionCppAdoptionJournal(
    Guid BuildId,
    SdGpuBackend NewBackend,
    string NewCommit,
    bool HadPreviousDestination,
    StableDiffusionInstalledRuntimeState? PreviousState,
    StableDiffusionInstalledRuntimeState NewState);
