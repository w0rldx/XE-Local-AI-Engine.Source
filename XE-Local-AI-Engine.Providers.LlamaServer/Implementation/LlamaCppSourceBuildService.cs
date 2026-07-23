namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaCppSourceBuildService" />. Orchestrates a single-flight, cancellable, background source
///     <c>llama-server</c> build and adopts the result as a managed runtime. Every subprocess runs under a scrubbed,
///     allowlisted environment (<c>[secHIGH-2]</c>) in an owner-only (0700) work directory inside the cache root — never
///     <c>/tmp</c> (<c>[secHIGH-3]</c>, Locked #8). The clone source URL + tag are constants and the checked-out commit is
///     verified == the pinned SHA before any cmake runs (<c>[secHIGH-1]</c>). On any failure the partial tree is deleted
///     and nothing is recorded (no silent CPU fallback).
/// </summary>
public sealed partial class LlamaCppSourceBuildService : ILlamaCppSourceBuildService, IDisposable
{
    private const string ManagedServerFileName = "llama-server";
    private const string ManifestFileName = ".source-build-manifest.json";

    // Conservative fallback compute-architecture set when nvidia-smi's compute_cap can't be read/validated. [secMED-1]
    private const string DefaultCudaArchitectures = "75;86;89";

    // -j cap: parallel build jobs are min(nproc, this) to bound peak memory/CPU during the build. [secMED-5]
    private const int MaxBuildJobs = 8;

    // The number of streamed log lines retained for the status GET (the hub streams every line live).
    private const int LogRingCapacity = 400;

    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(120);

    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ILlamaCppSourceBuildActivity _buildActivity;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly IActiveSourceBuildSignal _activeSignal;
    private readonly string _cacheRoot;
    private readonly string _homeDirectory;
    private readonly ILogger<LlamaCppSourceBuildService> _logger;
    private readonly ILlamaCppSourceBuildPrerequisiteProbe _prerequisiteProbe;
    private readonly ILlamaCppSourceBuildEventPublisher _publisher;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly Lock _publishLock = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Lock _stateLock = new();

    private Task? _activeBuildTask;
    private CancellationTokenSource? _buildCts;
    private DateTimeOffset? _completedAtUtc;
    private LlamaCppSourceBuildDescriptor? _currentBuild;
    private bool _isRunning;
    private long _logStartSequence;
    private List<string> _logLines = [];
    private long _nextLogSequence;
    private LlamaCppSourceBuildPhase _phase = LlamaCppSourceBuildPhase.Idle;
    private Task _publishTail = Task.CompletedTask;
    private string? _sanitizedError;
    private DateTimeOffset? _startedAtUtc;

    /// <summary>Creates the build service with the process-wide source-build activity reservation.</summary>
    public LlamaCppSourceBuildService(ILlamaCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        ILlamaCppBinaryManager binaryManager,
        IInstalledRuntimeStore installedRuntimeStore,
        IActiveSourceBuildSignal activeSignal,
        ILlamaServerProcessSupervisor supervisor,
        ILlamaCppSourceBuildActivity buildActivity,
        ILlamaCppSourceBuildEventPublisher publisher,
        ILogger<LlamaCppSourceBuildService> logger)
        : this(prerequisiteProbe, binaryManager, installedRuntimeStore, activeSignal, supervisor, buildActivity, publisher, logger, DefaultCacheRoot())
    {
    }

    /// <summary>Test seam: pins the cache root and shares a source-build activity reservation with the supervisor.</summary>
    internal LlamaCppSourceBuildService(ILlamaCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        ILlamaCppBinaryManager binaryManager,
        IInstalledRuntimeStore installedRuntimeStore,
        IActiveSourceBuildSignal activeSignal,
        ILlamaServerProcessSupervisor supervisor,
        ILlamaCppSourceBuildActivity buildActivity,
        ILlamaCppSourceBuildEventPublisher publisher,
        ILogger<LlamaCppSourceBuildService> logger,
        string cacheRoot)
    {
        _prerequisiteProbe = prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _activeSignal = activeSignal ?? throw new ArgumentNullException(nameof(activeSignal));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _buildActivity = buildActivity ?? throw new ArgumentNullException(nameof(buildActivity));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _homeDirectory = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
    }

    private string SourceBuildRoot => Path.Combine(_cacheRoot, "llama.cpp", "source-build");

    private string WorkDirectory => Path.Combine(SourceBuildRoot, ".work");

    private string MarkerPath => Path.Combine(WorkDirectory, ".build-in-progress");

    public void Dispose()
    {
        CancellationTokenSource? buildCts;
        lock (_stateLock)
        {
            buildCts = _buildCts;
        }

        try
        {
            buildCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A completed build may dispose its token concurrently with application teardown.
        }

        buildCts?.Dispose();
        _startGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<LlamaCppSourceBuildStartResult> StartAsync(LlamaCppSourceBuildRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new LlamaRuntimeException("In-app source builds are available on Linux only.");
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        TaskCompletionSource? startSignal = null;
        try
        {
            var normalized = LlamaCppSourceBuildRequestValidation.Normalize(request);
            var variant = ToVariant(normalized.Backend);
            var revisionMode = normalized.Commit is not null
                ? LlamaCppSourceRevisionMode.ExplicitCommit
                : LlamaCppSourceRevisionMode.DefaultBranch;
            if (normalized.Commit is null && normalized.Source == LlamaCppSourceSelection.Official)
            {
                revisionMode = LlamaCppSourceRevisionMode.EnginePinned;
            }
            var descriptor = new LlamaCppSourceBuildDescriptor(variant,
                normalized.Source,
                normalized.Repository!,
                revisionMode,
                normalized.Commit,
                ResolvedCommit: revisionMode == LlamaCppSourceRevisionMode.EnginePinned ? LlamaCppReleasePins.PinnedSourceCommitSha : null)
            {
                BuildId = Guid.NewGuid()
            };
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return new LlamaCppSourceBuildStartResult(LlamaCppSourceBuildStartOutcome.AlreadyRunning);
                }
            }

            // Recovery, prerequisite validation, and slot claim are one serialized start transaction. A losing caller must
            // not delete the winning build's .work tree or repeat expensive prerequisite probes.
            await RecoverAsync(ct).ConfigureAwait(false);
            var report = await _prerequisiteProbe.ProbeAsync(normalized.Backend, ct).ConfigureAwait(false);
            if (!report.CanBuild)
            {
                var outcome = report.Items.Any(static item =>
                    string.Equals(item.Key, "free-disk", StringComparison.Ordinal) && !item.Satisfied)
                    ? LlamaCppSourceBuildStartOutcome.InsufficientDisk
                    : LlamaCppSourceBuildStartOutcome.MissingPrerequisites;
                return new LlamaCppSourceBuildStartResult(outcome, report);
            }

            var mutationLease = await _supervisor.TryAcquireRuntimeMutationLeaseAsync(ct).ConfigureAwait(false);
            if (mutationLease is null)
            {
                var processCount = _supervisor.CountRunningProcesses();
                return processCount > 0
                    ? new LlamaCppSourceBuildStartResult(LlamaCppSourceBuildStartOutcome.ProcessesRunning, RunningProcessCount: processCount)
                    : new LlamaCppSourceBuildStartResult(LlamaCppSourceBuildStartOutcome.RuntimeBusy);
            }

            await using (mutationLease.ConfigureAwait(false))
            {
                if (!_buildActivity.TryReserve(descriptor.BuildId))
                {
                    return new LlamaCppSourceBuildStartResult(LlamaCppSourceBuildStartOutcome.RuntimeBusy);
                }

                try
                {
                    var buildCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                    startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var buildTask = Task.Run(async () =>
                    {
                        await startSignal.Task.ConfigureAwait(false);
                        // StartAsync rejects non-Linux callers before probing or reserving activity. This local guard also
                        // carries that invariant into the detached lambda for the platform analyzer.
                        if (OperatingSystem.IsLinux())
                        {
                            await RunBuildAsync(descriptor, buildCts.Token).ConfigureAwait(false);
                        }
                    }, CancellationToken.None);
                    lock (_stateLock)
                    {
                        _isRunning = true;
                        _phase = LlamaCppSourceBuildPhase.Cloning;
                        _logLines = [];
                        _logStartSequence = 0;
                        _nextLogSequence = 0;
                        _sanitizedError = null;
                        _currentBuild = descriptor;
                        _startedAtUtc = DateTimeOffset.UtcNow;
                        _completedAtUtc = null;
                        _buildCts?.Dispose();
                        _buildCts = buildCts;
                        _activeBuildTask = buildTask;
                    }
                }
                catch
                {
                    _buildActivity.TryRelease(descriptor.BuildId);
                    throw;
                }
            }
        }
        finally
        {
            _startGate.Release();
        }

        // Release the start transaction before allowing the detached build to touch its work tree.
        startSignal.SetResult();
        return new LlamaCppSourceBuildStartResult(LlamaCppSourceBuildStartOutcome.Started);
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Task? activeBuildTask;
            CancellationTokenSource? buildCts;
            lock (_stateLock)
            {
                buildCts = _buildCts;
                activeBuildTask = _activeBuildTask;
            }

            if (buildCts is not null)
            {
                try
                {
                    await buildCts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // The build completed while shutdown was taking its snapshot.
                }
            }

            if (activeBuildTask is not null)
            {
                await activeBuildTask.WaitAsync(ct).ConfigureAwait(false);
            }

            await FlushPublisherAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <inheritdoc />
    public LlamaCppSourceBuildStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new LlamaCppSourceBuildStatus(_phase,
                _isRunning,
                Terminal: _phase is LlamaCppSourceBuildPhase.Completed or LlamaCppSourceBuildPhase.Cancelled or LlamaCppSourceBuildPhase.Failed,
                LogLines: [.. _logLines],
                _logStartSequence,
                _sanitizedError,
                _currentBuild,
                _startedAtUtc,
                _completedAtUtc);
        }
    }

    /// <inheritdoc />
    public bool Cancel()
    {
        return CancelUnderLock(static _ => true);
    }

    /// <inheritdoc />
    public bool CancelLegacyPinnedCuda()
    {
        return CancelUnderLock(static descriptor => descriptor.IsLegacyPinnedCuda());
    }

    private bool CancelUnderLock(Func<LlamaCppSourceBuildDescriptor?, bool> predicate)
    {
        lock (_stateLock)
        {
            if (!_isRunning || _buildCts is null || !predicate(_currentBuild))
            {
                return false;
            }

            try
            {
                _buildCts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public async Task RecoverAsync(CancellationToken ct)
    {
        DeleteDirectoryRequired(WorkDirectory);
        DeleteDirectoryRequired(Path.Combine(SourceBuildRoot, ".staging"));
        await ReconcileActiveAndBackupAsync(ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("linux")]
    private async Task RunBuildAsync(LlamaCppSourceBuildDescriptor descriptor, CancellationToken ct)
    {
        var sourceCudaRoot = SourceBuildRoot;
        var workDir = WorkDirectory;
        var cloneDir = Path.Combine(workDir, "llama.cpp");
        var buildDir = Path.Combine(cloneDir, "build");
        var tag = LlamaCppReleasePins.PinnedTag;
        var finalTagDir = Path.Combine(sourceCudaRoot, "active");
        // Sibling staging + backup dirs (same filesystem as finalTagDir → moves are atomic). The new build is validated in
        // staging and the previous runtime is parked in backup, so a FAILED rebuild never loses a working runtime.
        var stagingTagDir = Path.Combine(sourceCudaRoot, ".staging");
        var backupTagDir = Path.Combine(sourceCudaRoot, ".backup");

        try
        {
            // Owner-only (0700) work dir inside the cache root — never /tmp. [secHIGH-3, Locked #8]
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(stagingTagDir);
            TryDeleteDirectory(backupTagDir);
            CreateOwnerOnlyDirectory(sourceCudaRoot);
            CreateOwnerOnlyDirectory(workDir);
            await File.WriteAllTextAsync(MarkerPath, DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);

            var isolatedHome = Path.Combine(workDir, ".home");
            var isolatedTmp = Path.Combine(workDir, ".tmp");
            CreateOwnerOnlyDirectory(isolatedHome);
            CreateOwnerOnlyDirectory(isolatedTmp);
            var environment = BuildScrubbedEnvironment(isolatedHome, isolatedTmp);

            // 1. Clone the pinned source at the pinned tag (NO submodules; URL+tag are constants).
            SetPhase(LlamaCppSourceBuildPhase.Cloning);
            var cloneExit = await CloneSourceAsync(descriptor, cloneDir, environment, workDir, ct).ConfigureAwait(false);
            if (cloneExit != 0)
            {
                throw new LlamaRuntimeException("Cloning the llama.cpp source failed.");
            }

            // 2. Verify the checked-out commit == the pinned SHA BEFORE any cmake runs. [secHIGH-1]
            SetPhase(LlamaCppSourceBuildPhase.Verifying);
            var (revExit, revOutput) = await RunCaptureAsync("git",
                ["-C", cloneDir, "rev-parse", "HEAD"],
                environment,
                workDir,
                ShortCommandTimeout,
                ct).ConfigureAwait(false);
            var checkedOut = revOutput.Trim();
            var expectedCommit = descriptor.RevisionMode switch
            {
                LlamaCppSourceRevisionMode.EnginePinned => LlamaCppReleasePins.PinnedSourceCommitSha,
                LlamaCppSourceRevisionMode.ExplicitCommit => descriptor.RequestedCommit,
                _ => null
            };
            if (revExit != 0 || !FullCommitRegex().IsMatch(checkedOut)
                || expectedCommit is not null && !string.Equals(checkedOut, expectedCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new LlamaRuntimeException("The cloned source did not match the pinned commit; the build was aborted before configuring.");
            }

            AppendLog($"Verified source commit {checkedOut}.");
            descriptor = descriptor with { ResolvedCommit = Convert.ToHexStringLower(Convert.FromHexString(checkedOut)) };
            lock (_stateLock)
            {
                _currentBuild = descriptor;
            }

            // 3. Detect + validate the compute architecture. [secMED-1]
            var architectures = descriptor.Variant == GpuVariant.Cuda
                ? await ResolveCudaArchitecturesAsync(environment, workDir, ct).ConfigureAwait(false)
                : null;

            // 4. cmake configure.
            SetPhase(LlamaCppSourceBuildPhase.Configuring);
            var configureExit = await RunStreamingStepAsync("cmake",
                BuildConfigureArguments(buildDir, cloneDir, descriptor.Variant, architectures),
                environment,
                cloneDir,
                ConfigureTimeout,
                ct).ConfigureAwait(false);
            if (configureExit != 0)
            {
                throw new LlamaRuntimeException("Configuring the llama.cpp source build failed.");
            }

            // 5. cmake build (jobs capped). [secMED-5]
            SetPhase(LlamaCppSourceBuildPhase.Building);
            var jobs = Math.Max(1, Math.Min(Environment.ProcessorCount, MaxBuildJobs));
            var buildExit = await RunStreamingStepAsync("cmake",
                ["--build", buildDir, "--target", "llama-server", "-j", jobs.ToString()],
                environment,
                cloneDir,
                BuildTimeout,
                ct).ConfigureAwait(false);
            if (buildExit != 0)
            {
                throw new LlamaRuntimeException("Compiling the source-built llama-server failed.");
            }

            // 6. Stage the built tree, harden it, then swap it into place — the previous runtime is only removed AFTER the
            //    new build validates + adopts, so a failed rebuild leaves the working managed runtime untouched.
            SetPhase(LlamaCppSourceBuildPhase.Adopting);
            var builtBin = Path.Combine(buildDir, "bin");
            if (!File.Exists(Path.Combine(builtBin, ManagedServerFileName)))
            {
                throw new LlamaRuntimeException("The build did not produce the expected server executable.");
            }

            // Stage the placed tree at a sibling dir (same filesystem → atomic moves) and harden it there.
            var stagingBuildDir = Path.Combine(stagingTagDir, "build");
            CreateOwnerOnlyDirectory(stagingTagDir);
            Directory.Move(buildDir, stagingBuildDir);
            ValidateTreeLinks(stagingTagDir);
            HardenTree(sourceCudaRoot, stagingTagDir);
            await WriteManifestAsync(stagingTagDir, descriptor, tag, ct).ConfigureAwait(false);
            HardenTree(sourceCudaRoot, stagingTagDir);

            // Swap into place last: park any previous runtime in the backup, move the staged tree in, then validate + adopt
            // the FINAL binary. On any failure, roll the previous runtime back so the working runtime is never lost.
            await using var mutationLease = await _supervisor.TryAcquireRuntimeMutationLeaseAsync(ct).ConfigureAwait(false)
                ?? throw new LlamaRuntimeException("A llama-server process started while the build was running; stop it before adopting the new runtime.");
            var hadPrevious = Directory.Exists(finalTagDir);
            try
            {
                if (hadPrevious)
                {
                    Directory.Move(finalTagDir, backupTagDir);
                }
                Directory.Move(stagingTagDir, finalTagDir);
                var finalBin = Path.Combine(finalTagDir, "build", "bin");
                await _binaryManager.AdoptSourceBuildAsync(finalBin,
                    tag,
                    descriptor.Variant,
                    descriptor.Repository,
                    descriptor.ResolvedCommit!,
                    descriptor.RevisionMode,
                    descriptor.RequestedCommit,
                    descriptor.Source,
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Discard the failed build and restore the previous working runtime in place.
                TryDeleteDirectory(stagingTagDir);
                TryDeleteDirectory(finalTagDir);
                if (hadPrevious)
                {
                    Directory.Move(backupTagDir, finalTagDir);
                }

                throw;
            }

            // Success: drop the backup + work dir (the placed tree is the only artifact kept).
            TryDeleteDirectory(backupTagDir);
            TryDeleteDirectory(workDir);
            await SetTerminalAsync(LlamaCppSourceBuildPhase.Completed, sanitizedError: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The swap step already rolled finalTagDir back on failure; only the partial work + staging trees are dropped.
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(stagingTagDir);
            await SetTerminalAsync(LlamaCppSourceBuildPhase.Cancelled, sanitizedError: null).ConfigureAwait(false);
        }
        catch (LlamaRuntimeException exception)
        {
            _logger.LogWarning(exception, "The in-app source build failed.");
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(stagingTagDir);
            await SetTerminalAsync(LlamaCppSourceBuildPhase.Failed, exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The in-app source build failed unexpectedly.");
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(stagingTagDir);
            await SetTerminalAsync(LlamaCppSourceBuildPhase.Failed, "The source build failed unexpectedly.").ConfigureAwait(false);
        }
        finally
        {
            _buildActivity.TryRelease(descriptor.BuildId);
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task<int> CloneSourceAsync(LlamaCppSourceBuildDescriptor descriptor,
        string cloneDir,
        IReadOnlyDictionary<string, string> environment,
        string workDir,
        CancellationToken ct)
    {
        var commands = BuildCloneCommands(descriptor, cloneDir);
        if (descriptor.RevisionMode == LlamaCppSourceRevisionMode.ExplicitCommit)
        {
            Directory.CreateDirectory(cloneDir);
            for (var index = 0; index < commands.Count; index++)
            {
                var timeout = index == 2 ? CloneTimeout : ShortCommandTimeout;
                var exit = await RunStreamingStepAsync("git", commands[index], environment, workDir, timeout, ct).ConfigureAwait(false);
                if (exit != 0)
                {
                    return exit;
                }
            }
            return 0;
        }

        return await RunStreamingStepAsync("git", commands[0], environment, workDir, CloneTimeout, ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<IReadOnlyList<string>> BuildCloneCommands(LlamaCppSourceBuildDescriptor descriptor, string cloneDir)
    {
        if (descriptor.RevisionMode == LlamaCppSourceRevisionMode.ExplicitCommit)
        {
            return
            [
                ["-C", cloneDir, "init"],
                ["-C", cloneDir, "remote", "add", "origin", descriptor.Repository],
                ["-C", cloneDir, "fetch", "--depth", "1", "--no-tags", "origin", descriptor.RequestedCommit!],
                ["-C", cloneDir, "checkout", "--detach", descriptor.RequestedCommit!]
            ];
        }

        return descriptor.RevisionMode == LlamaCppSourceRevisionMode.EnginePinned
            ? [["clone", "--depth", "1", "--no-recurse-submodules", "--branch", LlamaCppReleasePins.PinnedTag, descriptor.Repository, cloneDir]]
            : [["clone", "--depth", "1", "--no-recurse-submodules", descriptor.Repository, cloneDir]];
    }

    internal static IReadOnlyList<string> BuildConfigureArguments(string buildDir, string cloneDir, GpuVariant variant, string? architectures)
    {
        var args = new List<string>
        {
            "-B", buildDir, "-S", cloneDir,
            "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_BUILD_RPATH_USE_ORIGIN=ON", "-DLLAMA_CURL=OFF",
            $"-DGGML_CUDA={(variant == GpuVariant.Cuda ? "ON" : "OFF")}",
            $"-DGGML_VULKAN={(variant == GpuVariant.Vulkan ? "ON" : "OFF")}"
        };
        if (variant == GpuVariant.Cuda)
        {
            args.Add($"-DCMAKE_CUDA_ARCHITECTURES={architectures}");
        }

        return args;
    }

    private async Task ReconcileActiveAndBackupAsync(CancellationToken ct)
    {
        var active = Path.Combine(SourceBuildRoot, "active");
        var backup = Path.Combine(SourceBuildRoot, ".backup");
        var installed = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        var sourceRecord = installed?.SourceBuildPath is { Length: > 0 };

        if (!Directory.Exists(active) && !Directory.Exists(backup) && IsPreProvenanceLegacyRecord(installed))
        {
            if (await ValidateLegacyRecordAsync(installed!, ct).ConfigureAwait(false))
            {
                _activeSignal.SetActive(GpuVariant.Cuda);
                return;
            }

            await _installedRuntimeStore.DeleteAsync(ct).ConfigureAwait(false);
            _activeSignal.Clear();
            return;
        }

        var canonicalActiveBin = Path.Combine(active, "build", "bin");
        var recordTargetsActive = sourceRecord && PathsEqual(installed!.SourceBuildPath!, canonicalActiveBin);
        var activeMatches = recordTargetsActive && await TreeMatchesRecordAsync(active, installed!, ct).ConfigureAwait(false);
        var backupState = recordTargetsActive
            ? installed! with { SourceBuildPath = Path.Combine(backup, "build", "bin") }
            : installed!;
        var backupMatches = recordTargetsActive && await TreeMatchesRecordAsync(backup, backupState, ct).ConfigureAwait(false);

        if (Directory.Exists(active) && Directory.Exists(backup))
        {
            if (activeMatches)
            {
                DeleteDirectoryRequired(backup);
            }
            else if (backupMatches)
            {
                DeleteDirectoryRequired(active);
                _activeSignal.Clear();
                Directory.Move(backup, active);
                await WriteReconciledStateAsync(installed!, active, ct).ConfigureAwait(false);
            }
            else
            {
                DeleteDirectoryRequired(active);
                _activeSignal.Clear();
                DeleteDirectoryRequired(backup);
                await ClearSourceRecordAsync(sourceRecord, ct).ConfigureAwait(false);
            }
        }
        else if (Directory.Exists(backup))
        {
            if (backupMatches)
            {
                Directory.Move(backup, active);
                await WriteReconciledStateAsync(installed!, active, ct).ConfigureAwait(false);
            }
            else
            {
                DeleteDirectoryRequired(backup);
                _activeSignal.Clear();
                await ClearSourceRecordAsync(sourceRecord, ct).ConfigureAwait(false);
            }
        }
        else if (Directory.Exists(active))
        {
            if (!activeMatches)
            {
                DeleteDirectoryRequired(active);
                _activeSignal.Clear();
                await ClearSourceRecordAsync(sourceRecord, ct).ConfigureAwait(false);
            }
        }
        else
        {
            await ClearSourceRecordAsync(sourceRecord, ct).ConfigureAwait(false);
        }

        var current = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (current?.SourceBuildPath is { Length: > 0 })
        {
            _activeSignal.SetActive(current.Variant);
        }
    }

    private static async Task<bool> TreeMatchesRecordAsync(string tree, InstalledRuntimeState state, CancellationToken ct)
    {
        if (!Directory.Exists(tree)
            || state.SourceBuildPath is not { Length: > 0 }
            || !PathsEqual(state.SourceBuildPath, Path.Combine(tree, "build", "bin"))
            || state.SourceRepository is null
            || state.SourceCommit is null
            || state.SourceRevisionMode is null)
        {
            return false;
        }

        var server = Path.Combine(tree, "build", "bin", ManagedServerFileName);
        var manifestPath = Path.Combine(tree, ManifestFileName);
        if (!File.Exists(server) || new FileInfo(server).LinkTarget is not null || !File.Exists(manifestPath))
        {
            return false;
        }

        SourceBuildManifest? manifest;
        try
        {
            await using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            manifest = await JsonSerializer.DeserializeAsync<SourceBuildManifest>(manifestStream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return false;
        }

        var expectedSource = state.SourceSelection
            ?? (string.Equals(state.SourceRepository, LlamaCppSourceBuildRequestValidation.OfficialRepository, StringComparison.Ordinal)
                ? LlamaCppSourceSelection.Official
                : LlamaCppSourceSelection.Custom);
        if (!LlamaCppSourceBuildRequestValidation.HasValidOfficialProvenance(expectedSource,
                state.SourceRepository,
                state.SourceRevisionMode,
                state.SourceRequestedCommit,
                state.SourceCommit)
            || manifest is null
            || !LlamaCppSourceBuildRequestValidation.HasValidOfficialProvenance(manifest.Source,
                manifest.Repository,
                manifest.RevisionMode,
                manifest.RequestedCommit,
                manifest.ResolvedCommit)
            || manifest.Variant != state.Variant
            || manifest.Source != expectedSource
            || manifest.RevisionMode != state.SourceRevisionMode
            || !string.Equals(manifest.Tag, state.Tag, StringComparison.Ordinal)
            || !string.Equals(manifest.Repository, state.SourceRepository, StringComparison.Ordinal)
            || !string.Equals(manifest.ResolvedCommit, state.SourceCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.RequestedCommit, state.SourceRequestedCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.BinarySha256, state.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            ValidateTreeLinks(tree);
        }
        catch (LlamaRuntimeException)
        {
            return false;
        }
        await using var stream = new FileStream(server, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return string.Equals(hash, state.Sha256, StringComparison.OrdinalIgnoreCase)
            && await ValidateBinaryBackendAsync(server, state.Variant, ct).ConfigureAwait(false);
    }

    private bool IsPreProvenanceLegacyRecord(InstalledRuntimeState? state)
    {
        return state is
            {
                SourceRepository: null,
                SourceCommit: null,
                SourceRevisionMode: null,
                SourceRequestedCommit: null,
                SourceSelection: null
            }
            && state.IsLegacyPinnedCuda(_cacheRoot);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async Task<bool> ValidateLegacyRecordAsync(InstalledRuntimeState state, CancellationToken ct)
    {
        var server = Path.Combine(state.SourceBuildPath!, ManagedServerFileName);
        if (!File.Exists(server) || new FileInfo(server).LinkTarget is not null)
        {
            return false;
        }

        try
        {
            ValidateTreeLinks(state.SourceBuildPath!);
        }
        catch (LlamaRuntimeException)
        {
            return false;
        }
        await using var stream = new FileStream(server, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return string.Equals(hash, state.Sha256, StringComparison.OrdinalIgnoreCase)
            && await ValidateBinaryBackendAsync(server, GpuVariant.Cuda, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ValidateBinaryBackendAsync(string server, GpuVariant variant, CancellationToken ct)
    {
        if (!await RunValidationCommandAsync(server, "--version", expectedDevicePrefix: null, ct).ConfigureAwait(false))
        {
            return false;
        }

        return variant == GpuVariant.Cpu
            || await RunValidationCommandAsync(server, "--list-devices", variant == GpuVariant.Cuda ? "CUDA" : "Vulkan", ct).ConfigureAwait(false);
    }

    private static async Task<bool> RunValidationCommandAsync(string server, string argument, string? expectedDevicePrefix, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(server)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(server) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ShortCommandTimeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = string.Concat(await stdout.ConfigureAwait(false), "\n", await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
            {
                return false;
            }

            return expectedDevicePrefix is null || output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => RecoveryDeviceLineRegex().IsMatch(line)
                             && line.TrimStart().StartsWith(expectedDevicePrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            TryKill(process);
        }
    }

    private static async Task WriteManifestAsync(string tree, LlamaCppSourceBuildDescriptor descriptor, string tag, CancellationToken ct)
    {
        var server = Path.Combine(tree, "build", "bin", ManagedServerFileName);
        await using var binary = new FileStream(server, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(binary, ct).ConfigureAwait(false));
        var manifest = new SourceBuildManifest(tag,
            descriptor.Variant,
            descriptor.Source,
            descriptor.Repository,
            descriptor.RevisionMode,
            descriptor.RequestedCommit,
            descriptor.ResolvedCommit!,
            sha);
        var path = Path.Combine(tree, ManifestFileName);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: ct).ConfigureAwait(false);
    }

    private Task WriteReconciledStateAsync(InstalledRuntimeState state, string active, CancellationToken ct)
    {
        return _installedRuntimeStore.WriteAsync(state with { SourceBuildPath = Path.Combine(active, "build", "bin") }, ct);
    }

    private async Task ClearSourceRecordAsync(bool sourceRecord, CancellationToken ct)
    {
        if (sourceRecord)
        {
            await _installedRuntimeStore.DeleteAsync(ct).ConfigureAwait(false);
            _activeSignal.Clear();
        }
    }

    private sealed record SourceBuildManifest(
        string Tag,
        GpuVariant Variant,
        LlamaCppSourceSelection Source,
        string Repository,
        LlamaCppSourceRevisionMode RevisionMode,
        string? RequestedCommit,
        string ResolvedCommit,
        string BinarySha256);

    private static GpuVariant ToVariant(LlamaCppSourceBackend backend)
    {
        return backend switch
        {
            LlamaCppSourceBackend.Cpu => GpuVariant.Cpu,
            LlamaCppSourceBackend.Vulkan => GpuVariant.Vulkan,
            LlamaCppSourceBackend.Cuda => GpuVariant.Cuda,
            _ => throw new LlamaRuntimeException("The source-build backend is invalid.")
        };
    }

    // nvidia-smi compute_cap → whitelisted CUDA architecture list, else the conservative default set. [secMED-1]
    private static async Task<string> ResolveCudaArchitecturesAsync(IReadOnlyDictionary<string, string> environment, string workDir, CancellationToken ct)
    {
        try
        {
            var (exit, output) = await RunCaptureAsync("nvidia-smi",
                ["--query-gpu=compute_cap", "--format=csv,noheader"],
                environment,
                workDir,
                ShortCommandTimeout,
                ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return DefaultCudaArchitectures;
            }

            return ParseCudaArchitectures(output);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DefaultCudaArchitectures;
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

            var architecture = major * 10 + minor;
            if (!IsSupportedCudaArchitecture(architecture))
            {
                return DefaultCudaArchitectures;
            }

            values.Add(architecture);
        }

        return values.Count == 0 ? DefaultCudaArchitectures : string.Join(';', values);
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

    [SupportedOSPlatform("linux")]
    private Task<int> RunStreamingStepAsync(string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string workDir,
        TimeSpan timeout,
        CancellationToken ct)
    {
        return StreamingProcessRunner.RunAsync(file, args, environment, workDir, AppendLog, timeout, ct);
    }

    // Captures (rather than streams) a short command's stdout under the scrubbed env, bounded + tree-killed.
    private static async Task<(int ExitCode, string Stdout)> RunCaptureAsync(string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string workDir,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment.Clear();
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        if (!process.Start())
        {
            return (-1, string.Empty);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKill(process);
            return (-1, string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
    }

    // Scrubbed, allowlisted build environment: ONLY these keys pass through; everything else (LD_PRELOAD, LD_LIBRARY_PATH,
    // CC, CXX, CUDAHOSTCXX, CMAKE_*_LAUNCHER, GIT_SSH_COMMAND, GIT_PROXY_COMMAND, GIT_EXTERNAL_DIFF, app secrets) is
    // dropped by construction. [secHIGH-2, Locked #7]
    private static Dictionary<string, string> BuildScrubbedEnvironment(string isolatedHome, string isolatedTmp)
    {
        string[] allowlist = ["PATH", "LANG", "LC_ALL", "CUDA_HOME", "CUDA_PATH"];
        var scrubbed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in allowlist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                scrubbed[key] = value;
            }
        }

        scrubbed["HOME"] = isolatedHome;
        scrubbed["TMPDIR"] = isolatedTmp;
        scrubbed["GIT_CONFIG_NOSYSTEM"] = "1";
        scrubbed["GIT_TERMINAL_PROMPT"] = "0";
        scrubbed["GIT_ASKPASS"] = "/bin/false";
        scrubbed["SSH_ASKPASS"] = "/bin/false";
        scrubbed["GIT_CONFIG_COUNT"] = "2";
        scrubbed["GIT_CONFIG_KEY_0"] = "credential.helper";
        scrubbed["GIT_CONFIG_VALUE_0"] = string.Empty;
        scrubbed["GIT_CONFIG_KEY_1"] = "submodule.recurse";
        scrubbed["GIT_CONFIG_VALUE_1"] = "false";

        return scrubbed;
    }

    private void SetPhase(LlamaCppSourceBuildPhase phase)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _ = QueuePublish(new LlamaCppSourceBuildStatusHubEvent(phase.ToString(),
                [],
                _nextLogSequence,
                Terminal: false,
                SanitizedError: null,
                _currentBuild));
        }
    }

    private async Task SetTerminalAsync(LlamaCppSourceBuildPhase phase, string? sanitizedError)
    {
        Task publish;
        lock (_stateLock)
        {
            _phase = phase;
            _isRunning = false;
            _sanitizedError = sanitizedError;
            _completedAtUtc = DateTimeOffset.UtcNow;
            publish = QueuePublish(new LlamaCppSourceBuildStatusHubEvent(phase.ToString(),
                [],
                _nextLogSequence,
                Terminal: true,
                sanitizedError,
                _currentBuild));
        }

        await publish.ConfigureAwait(false);
    }

    // The streaming log sink: redact the cache-root/HOME prefix [secLOW-1], retain a bounded ring buffer for the status
    // GET, and push the line live to the hub. Thread-safe (both pipes call this concurrently).
    internal void AppendLog(string line)
    {
        var redacted = Redact(line);
        lock (_stateLock)
        {
            var appendedSequence = _nextLogSequence++;
            _logLines.Add(redacted);
            if (_logLines.Count > LogRingCapacity)
            {
                _logLines.RemoveRange(0, _logLines.Count - LogRingCapacity);
            }
            _logStartSequence = _nextLogSequence - _logLines.Count;

            _ = QueuePublish(new LlamaCppSourceBuildStatusHubEvent(_phase.ToString(),
                [redacted],
                appendedSequence,
                Terminal: false,
                SanitizedError: null,
                _currentBuild));
        }
    }

    private string Redact(string line)
    {
        var result = line;
        if (!string.IsNullOrEmpty(_cacheRoot))
        {
            result = result.Replace(_cacheRoot, "<cache>", StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(_homeDirectory))
        {
            result = result.Replace(_homeDirectory, "<home>", StringComparison.Ordinal);
        }

        return result;
    }

    internal Task FlushPublisherAsync()
    {
        lock (_publishLock)
        {
            return _publishTail;
        }
    }

    private Task QueuePublish(LlamaCppSourceBuildStatusHubEvent statusEvent)
    {
        lock (_publishLock)
        {
            _publishTail = PublishObservedAsync(_publishTail, statusEvent);
            return _publishTail;
        }
    }

    private async Task PublishObservedAsync(Task previous, LlamaCppSourceBuildStatusHubEvent statusEvent)
    {
        // Process stdout/stderr callbacks must never run publisher work inline; yield before joining the serialized tail.
        await Task.Yield();
        await previous.ConfigureAwait(false);
        try
        {
            await _publisher.PublishStatusAsync(statusEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Publishing a source-build status event failed (non-fatal).");
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

    // Hardens every directory + file in the placed build tree (from the source-cuda root down) so the managed path-chain
    // validator passes: owner-only directories, owner read/exec files (binary keeps its exec bit). Never world-writable.
    private static void HardenTree(string sourceCudaRoot, string finalTagDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        SetDirectoryOwnerOnly(sourceCudaRoot);
        SetDirectoryOwnerOnly(finalTagDir);
        foreach (var dir in Directory.EnumerateDirectories(finalTagDir, "*", SearchOption.AllDirectories))
        {
            SetDirectoryOwnerOnly(dir);
        }

        foreach (var file in Directory.EnumerateFiles(finalTagDir, "*", SearchOption.AllDirectories))
        {
            var mode = File.GetUnixFileMode(file);
            // Strip group/other write so no ancestor/file is world- or group-writable; keep the owner exec bit on binaries.
            mode &= ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
            File.SetUnixFileMode(file, mode);
        }
    }

    private static void ValidateTreeLinks(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                throw new LlamaRuntimeException("The built runtime contains a linked directory.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                var current = entry;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (var hop = 0; hop < 32; hop++)
                {
                    if (!visited.Add(current))
                    {
                        throw new LlamaRuntimeException("The built runtime contains a cyclic library link.");
                    }

                    var info = new FileInfo(current);
                    if (info.LinkTarget is not { } target)
                    {
                        if (!info.Exists)
                        {
                            throw new LlamaRuntimeException("The built runtime contains a dangling library link.");
                        }
                        break;
                    }

                    if (Path.IsPathRooted(target))
                    {
                        throw new LlamaRuntimeException("The built runtime contains an absolute library link.");
                    }

                    current = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, target));
                    if (!current.StartsWith(rootPrefix, StringComparison.Ordinal) || Directory.Exists(current) || hop == 31)
                    {
                        throw new LlamaRuntimeException("The built runtime contains an unsafe library link.");
                    }
                }
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetDirectoryOwnerOnly(string path)
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
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
        catch (IOException)
        {
            // Best-effort cleanup of a partial build tree.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a partial build tree.
        }
    }

    private static void DeleteDirectoryRequired(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LlamaRuntimeException("A prior source-build directory could not be reconciled safely.", exception);
        }
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine");
    }

    [GeneratedRegex(@"^(?<major>[0-9]{1,2})\.(?<minor>[0-9])$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ComputeCapabilityRegex();

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FullCommitRegex();

    [GeneratedRegex(@"^\s*(?:CUDA|Vulkan)[0-9]+:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RecoveryDeviceLineRegex();
}
