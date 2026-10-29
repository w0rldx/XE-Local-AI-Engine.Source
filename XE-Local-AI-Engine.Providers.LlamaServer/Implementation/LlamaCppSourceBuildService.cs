namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaCppSourceBuildService" />: orchestrates a single-flight, cancellable, background source
///     <c>llama-server</c> build and adopts the result as a managed runtime.
/// </summary>
/// <remarks>
///     Every subprocess runs under a scrubbed, allowlisted environment (<c>[secHIGH-2]</c>) in an owner-only (0700)
///     work directory inside the cache root, never <c>/tmp</c> (<c>[secHIGH-3]</c>). The clone source URL and tag are
///     constants, and the checked-out commit is verified equal to the pinned SHA before any cmake runs
///     (<c>[secHIGH-1]</c>). On any failure the partial tree is deleted and nothing is recorded, so there is no silent
///     CPU fallback.
/// </remarks>
public sealed partial class LlamaCppSourceBuildService : ILlamaCppSourceBuildService, IDisposable
{
    internal const string ManagedFitParamsFileName = "llama-fit-params";
    private const string ManagedPerplexityFileName = LlamaCppToolBinaries.PerplexityName;
    private const string ManagedQuantizeFileName = LlamaCppToolBinaries.QuantizerName;
    internal const string ManagedServerFileName = "llama-server";
    internal const string ManifestFileName = ".source-build-manifest.json";

    // The number of streamed log lines retained for the status GET (the hub streams every line live).
    private const int LogRingCapacity = 400;

    private const string ScratchCleanupFailedMessage =
        "Source builds are unavailable until the previous build's scratch directories in the runtime cache can be removed. Check their ownership and permissions, then try again.";

    // A staging tree older than this belongs to no live acquisition, this node's or another's on the same cache root.
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);

    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ILlamaCppSourceBuildActivity _buildActivity;
    private readonly string _cacheRoot;
    private readonly string _homeDirectory;
    private readonly ILogger<LlamaCppSourceBuildService> _logger;
    private readonly ILlamaCppSourceBuildPrerequisiteProbe _prerequisiteProbe;
    private readonly LlamaCppSourceBuildReconciler _reconciler;
    private readonly ILlamaCppSourceBuildEventPublisher _publisher;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly Lock _publishLock = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly TimeProvider _timeProvider;

    private Task? _activeBuildTask;
    private CancellationTokenSource? _buildCts;
    private DateTimeOffset? _completedAtUtc;
    private LlamaCppSourceBuildDescriptor? _currentBuild;
    private bool _isRunning;
    private volatile bool _scratchCleanupFailed;
    internal const string InterruptedBuildMessage = "The node restarted during the build. Start it again to finish it.";

    private long _logStartSequence;
    private List<string> _logLines = [];
    private long _nextLogSequence;
    private LlamaCppSourceBuildPhase _phase = LlamaCppSourceBuildPhase.Idle;
    private volatile bool _shuttingDown;
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
        ILogger<LlamaCppSourceBuildService> logger,
        TimeProvider timeProvider)
        : this(prerequisiteProbe, binaryManager, installedRuntimeStore, activeSignal, supervisor, buildActivity, publisher, logger, timeProvider, RuntimeCacheDirectory.Resolve())
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
        TimeProvider timeProvider,
        string cacheRoot)
    {
        _prerequisiteProbe = prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _buildActivity = buildActivity ?? throw new ArgumentNullException(nameof(buildActivity));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _homeDirectory = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        _reconciler = new LlamaCppSourceBuildReconciler(installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore)),
            activeSignal ?? throw new ArgumentNullException(nameof(activeSignal)),
            cacheRoot,
            SourceBuildRoot,
            _logger);
    }

    private string SourceBuildRoot => Path.Combine(_cacheRoot, "llama.cpp", "source-build");

    private string WorkDirectory => Path.Combine(SourceBuildRoot, ".work");

    private string MarkerPath => Path.Combine(WorkDirectory, ".build-in-progress");

    // Outside the work tree, which recovery and the build's own cleanup both delete.
    private string InterruptedRecordPath => Path.Combine(SourceBuildRoot, ".build-interrupted");

    public void Dispose()
    {
        _shuttingDown = true;
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

            var descriptor = new LlamaCppSourceBuildDescriptor
            {
                Variant = variant,
                Source = normalized.Source,
                Repository = normalized.Repository!,
                RevisionMode = revisionMode,
                RequestedCommit = normalized.Commit,
                ResolvedCommit = revisionMode == LlamaCppSourceRevisionMode.EnginePinned ? LlamaCppReleasePins.PinnedSourceCommitSha : null,
                BuildId = Guid.NewGuid()
            };
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return new LlamaCppSourceBuildStartResult
                    {
                        Outcome = LlamaCppSourceBuildStartOutcome.AlreadyRunning
                    };
                }
            }

            // Recovery, prerequisite validation, and slot claim are one serialized start transaction. A losing caller must
            // not delete the winning build's .work tree or repeat expensive prerequisite probes.
            await RecoverAsync(ct).ConfigureAwait(false);
            if (_scratchCleanupFailed)
            {
                throw new LlamaRuntimeException(ScratchCleanupFailedMessage);
            }

            var report = await _prerequisiteProbe.ProbeAsync(normalized.Backend, ct).ConfigureAwait(false);
            if (!report.CanBuild)
            {
                var outcome = report.Items.Any(static item =>
                    string.Equals(item.Key, "free-disk", StringComparison.Ordinal) && !item.Satisfied)
                    ? LlamaCppSourceBuildStartOutcome.InsufficientDisk
                    : LlamaCppSourceBuildStartOutcome.MissingPrerequisites;
                return new LlamaCppSourceBuildStartResult
                {
                    Outcome = outcome,
                    Prerequisites = report
                };
            }

            var mutationLease = await _supervisor.AcquireRuntimeMutationLeaseOrNullAsync(ct).ConfigureAwait(false);
            if (mutationLease is null)
            {
                var processCount = _supervisor.CountRunningProcesses();
                return processCount > 0
                    ? new LlamaCppSourceBuildStartResult
                    {
                        Outcome = LlamaCppSourceBuildStartOutcome.ProcessesRunning,
                        RunningProcessCount = processCount
                    }
                    : new LlamaCppSourceBuildStartResult
                    {
                        Outcome = LlamaCppSourceBuildStartOutcome.RuntimeBusy
                    };
            }

            await using (mutationLease.ConfigureAwait(false))
            {
                if (!_buildActivity.TryReserve(descriptor.BuildId))
                {
                    return new LlamaCppSourceBuildStartResult
                    {
                        Outcome = LlamaCppSourceBuildStartOutcome.RuntimeBusy
                    };
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
                        _startedAtUtc = _timeProvider.GetUtcNow();
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
        return new LlamaCppSourceBuildStartResult
        {
            Outcome = LlamaCppSourceBuildStartOutcome.Started
        };
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken ct)
    {
        _shuttingDown = true;
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
            return new LlamaCppSourceBuildStatus
            {
                Phase = _phase,
                IsRunning = _isRunning,
                Terminal = _phase is LlamaCppSourceBuildPhase.Completed or LlamaCppSourceBuildPhase.Cancelled or LlamaCppSourceBuildPhase.Failed,
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
        return CancelUnderLock(static _ => true);
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
        RecordInterruptedBuild();

        // Scratch trees hold no state, so an undeletable one must not block host start; it only refuses new builds until a
        // later recovery removes it. Both are attempted so one failure does not leave the other behind.
        var workDeleted = TryDeleteScratchDirectory(WorkDirectory);
        var stagingDeleted = TryDeleteScratchDirectory(Path.Combine(SourceBuildRoot, ".staging"));
        _scratchCleanupFailed = !workDeleted || !stagingDeleted;

        SweepStalePrebuiltStaging();
        await _reconciler.ReconcileAsync(ct).ConfigureAwait(false);
    }

    // The marker outlives only a build the process never finished: every terminal path deletes the work tree. Without this
    // the next host answered a plain Idle and the operator was never told the build had died.
    private void RecordInterruptedBuild()
    {
        var shutdownRecord = File.Exists(InterruptedRecordPath);
        if (!shutdownRecord && !File.Exists(MarkerPath))
        {
            return;
        }

        if (shutdownRecord)
        {
            try
            {
                File.Delete(InterruptedRecordPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "The interrupted source-build record could not be removed; the next start reports the build again.");
            }
        }

        lock (_stateLock)
        {
            _phase = LlamaCppSourceBuildPhase.Failed;
            _sanitizedError = InterruptedBuildMessage;
            _completedAtUtc = _timeProvider.GetUtcNow();
        }

        _logger.LogWarning("A llama.cpp source build was interrupted by a node restart; it is reported as failed.");
    }

    private async Task RecordShutdownInterruptionAsync()
    {
        try
        {
            Directory.CreateDirectory(SourceBuildRoot);
            // Not the build's token: it is the one the shutdown just cancelled.
            await File.WriteAllTextAsync(InterruptedRecordPath, _timeProvider.GetUtcNow().ToString("O"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The source build stopped by the node shutdown could not be recorded; the next start shows it as idle.");
        }
    }

    // Prebuilt acquisitions killed mid-extract or mid-publish leave {variant}.{guid}.tmp trees nothing else removes. No
    // record lock: acquisitions do not hold it while staging, so only the age bound protects another node's live tree.
    private void SweepStalePrebuiltStaging()
    {
        try
        {
            var swept = LlamaCppBinaryManager.SweepStaleStagingDirectories(_cacheRoot, _timeProvider.GetUtcNow() - StaleStagingAge);
            if (swept > 0)
            {
                _logger.LogInformation("Removed {Count} stale llama.cpp runtime staging directories left by an interrupted acquisition.", swept);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Sweeping stale llama.cpp runtime staging directories failed; they are retried on the next start.");
        }
    }

    private bool TryDeleteScratchDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The source-build scratch directory {Path} could not be removed; new source builds are refused until it can be.", path);
            return false;
        }
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
            // Owner-only (0700) work directory inside the cache root — never /tmp. [secHIGH-3]
            BestEffortFile.TryDeleteDirectory(workDir);
            BestEffortFile.TryDeleteDirectory(stagingTagDir);
            BestEffortFile.TryDeleteDirectory(backupTagDir);
            CreateOwnerOnlyDirectory(sourceCudaRoot);
            CreateOwnerOnlyDirectory(workDir);
            await File.WriteAllTextAsync(MarkerPath, _timeProvider.GetUtcNow().ToString("O"), ct).ConfigureAwait(false);

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
            var (revExit, revOutput) = await ProcessCaptureRunner.RunAsync("git",
                ["-C", cloneDir, "rev-parse", "HEAD"],
                environment,
                workDir,
                SourceBuildPolicy.ShortCommandTimeout,
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
            descriptor = descriptor with
            {
                ResolvedCommit = Convert.ToHexStringLower(Convert.FromHexString(checkedOut))
            };
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
                SourceBuildPolicy.ConfigureTimeout,
                ct).ConfigureAwait(false);
            if (configureExit != 0)
            {
                throw new LlamaRuntimeException("Configuring the llama.cpp source build failed.");
            }

            // 5. cmake build (jobs capped). [secMED-5]
            SetPhase(LlamaCppSourceBuildPhase.Building);
            var jobs = Math.Max(1, Math.Min(Environment.ProcessorCount, SourceBuildPolicy.MaxBuildJobs));
            var buildExit = await RunStreamingStepAsync("cmake",
                ["--build", buildDir, "--target", ManagedServerFileName, ManagedFitParamsFileName, ManagedQuantizeFileName, ManagedPerplexityFileName, "-j", jobs.ToString()],
                environment,
                cloneDir,
                SourceBuildPolicy.BuildTimeout,
                ct).ConfigureAwait(false);
            if (buildExit != 0)
            {
                throw new LlamaRuntimeException("Compiling the source-built llama.cpp runtime failed.");
            }

            // 6. Stage the built tree, harden it, then swap it into place — the previous runtime is only removed AFTER the
            //    new build validates + adopts, so a failed rebuild leaves the working managed runtime untouched.
            SetPhase(LlamaCppSourceBuildPhase.Adopting);
            var builtBin = Path.Combine(buildDir, "bin");
            if (!File.Exists(Path.Combine(builtBin, ManagedServerFileName)))
            {
                throw new LlamaRuntimeException("The build did not produce the expected server executable.");
            }

            if (!File.Exists(Path.Combine(builtBin, ManagedFitParamsFileName)))
            {
                throw new LlamaRuntimeException("The build did not produce the expected fit-params helper.");
            }

            // Recorded, never enforced: the quantizer is off the inference path — only a training export needs it — so a build that produced everything else is still a
            // perfectly good serving runtime, and failing adoption here would trade a working runtime for a capability the operator may never use.
            if (LlamaCppToolBinaries.FindQuantizer(builtBin) is null)
            {
                AppendLog("The build produced no llama-quantize helper; training exports will not be able to quantize with this runtime.");
                _logger.LogWarning("The source build produced no {Quantizer} helper.", ManagedQuantizeFileName);
            }

            // Same posture for the perplexity tool: only a benchmark fidelity measurement needs it.
            if (LlamaCppToolBinaries.FindPerplexity(builtBin) is null)
            {
                AppendLog("The build produced no llama-perplexity helper; benchmark fidelity cannot be measured with this runtime.");
                _logger.LogWarning("The source build produced no {Perplexity} helper.", ManagedPerplexityFileName);
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
            await using var mutationLease = await _supervisor.AcquireRuntimeMutationLeaseOrNullAsync(ct).ConfigureAwait(false)
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
                    descriptor.ResolvedCommit,
                    descriptor.RevisionMode,
                    descriptor.RequestedCommit,
                    descriptor.Source,
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Discard the failed build and restore the previous working runtime in place.
                BestEffortFile.TryDeleteDirectory(stagingTagDir);
                BestEffortFile.TryDeleteDirectory(finalTagDir);
                if (hadPrevious)
                {
                    Directory.Move(backupTagDir, finalTagDir);
                }

                throw;
            }

            // Success: drop the backup + work dir (the placed tree is the only artifact kept).
            BestEffortFile.TryDeleteDirectory(backupTagDir);
            BestEffortFile.TryDeleteDirectory(workDir);
            await SetTerminalAsync(LlamaCppSourceBuildPhase.Completed, sanitizedError: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A host shutdown cancels the build too, and its cleanup removes the work tree and the marker with it; the record beside
            // the tree is what tells the next start the build died with the node, not by the operator's cancel.
            if (_shuttingDown)
            {
                await RecordShutdownInterruptionAsync().ConfigureAwait(false);
            }

            // The swap step already rolled finalTagDir back on failure; only the partial work + staging trees are dropped.
            BestEffortFile.TryDeleteDirectory(workDir);
            BestEffortFile.TryDeleteDirectory(stagingTagDir);
            await SetTerminalAsync(LlamaCppSourceBuildPhase.Cancelled, sanitizedError: null).ConfigureAwait(false);
        }
        catch (LlamaRuntimeException exception)
        {
            _logger.LogWarning(exception, "The in-app source build failed.");
            BestEffortFile.TryDeleteDirectory(workDir);
            BestEffortFile.TryDeleteDirectory(stagingTagDir);
            await SetTerminalAsync(LlamaCppSourceBuildPhase.Failed, exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The in-app source build failed unexpectedly.");
            BestEffortFile.TryDeleteDirectory(workDir);
            BestEffortFile.TryDeleteDirectory(stagingTagDir);
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
                var timeout = index == 2 ? SourceBuildPolicy.CloneTimeout : SourceBuildPolicy.ShortCommandTimeout;
                var exit = await RunStreamingStepAsync("git", commands[index], environment, workDir, timeout, ct).ConfigureAwait(false);
                if (exit != 0)
                {
                    return exit;
                }
            }

            return 0;
        }

        return await RunStreamingStepAsync("git", commands[0], environment, workDir, SourceBuildPolicy.CloneTimeout, ct).ConfigureAwait(false);
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
            "-B",
            buildDir,
            "-S",
            cloneDir,
            "-DCMAKE_BUILD_TYPE=Release",
            "-DCMAKE_BUILD_RPATH_USE_ORIGIN=ON",
            "-DLLAMA_CURL=OFF",
            $"-DGGML_CUDA={(variant == GpuVariant.Cuda ? "ON" : "OFF")}",
            $"-DGGML_VULKAN={(variant == GpuVariant.Vulkan ? "ON" : "OFF")}"
        };
        if (variant == GpuVariant.Cuda)
        {
            args.Add($"-DCMAKE_CUDA_ARCHITECTURES={architectures}");
        }

        return args;
    }

    private static async Task WriteManifestAsync(string tree, LlamaCppSourceBuildDescriptor descriptor, string tag, CancellationToken ct)
    {
        var server = Path.Combine(tree, "build", "bin", ManagedServerFileName);
        var sha = await FileSha256.ComputeAsync(server, ct).ConfigureAwait(false);
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

    internal sealed record SourceBuildManifest(
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
    private async Task<string> ResolveCudaArchitecturesAsync(IReadOnlyDictionary<string, string> environment, string workDir, CancellationToken ct)
    {
        try
        {
            var (exit, output) = await ProcessCaptureRunner.RunAsync("nvidia-smi",
                ["--query-gpu=compute_cap", "--format=csv,noheader"],
                environment,
                workDir,
                SourceBuildPolicy.ShortCommandTimeout,
                ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return SourceBuildPolicy.DefaultCudaArchitectures;
            }

            return SourceBuildPolicy.ParseCudaArchitectures(output);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "nvidia-smi compute-capability query failed; building for the default CUDA architectures.");
            return SourceBuildPolicy.DefaultCudaArchitectures;
        }
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

    // Scrubbed, allowlisted build environment: ONLY these keys pass through, everything else — LD_PRELOAD, LD_LIBRARY_PATH, CC, CXX, CUDAHOSTCXX, CMAKE_*_LAUNCHER,
    // GIT_SSH_COMMAND, GIT_PROXY_COMMAND, GIT_EXTERNAL_DIFF, app secrets — being dropped by construction. [secHIGH-2]
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

        // The checklist answers "CUDA compiler" from a conventional install that is not on PATH (see CudaToolkitLocator), so CMake is pinned to that same nvcc: a green
        // prerequisite can then never be followed by a build looking for the toolkit somewhere else. Child environment only — the host's is never touched.
        if (CudaToolkitLocator.FindNvccOutsidePath() is { } nvcc)
        {
            scrubbed["CUDACXX"] = nvcc;
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
            _ = QueuePublish(new LlamaCppSourceBuildStatusEvent
            {
                Phase = phase.ToString(),
                AppendedLogLines = [],
                AppendedLogStartSequence = _nextLogSequence,
                Terminal = false,
                SanitizedError = null,
                CurrentBuild = _currentBuild
            });
        }
    }

    private async Task SetTerminalAsync(LlamaCppSourceBuildPhase phase, string? sanitizedError)
    {
        // Deleted on its own: the work-tree delete swallows IO errors, and a marker left behind would report this finished build as interrupted.
        try
        {
            if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The source-build progress marker could not be removed; the next start reports this build as interrupted.");
        }

        Task publish;
        lock (_stateLock)
        {
            _phase = phase;
            _isRunning = false;
            _sanitizedError = sanitizedError;
            _completedAtUtc = _timeProvider.GetUtcNow();
            publish = QueuePublish(new LlamaCppSourceBuildStatusEvent
            {
                Phase = phase.ToString(),
                AppendedLogLines = [],
                AppendedLogStartSequence = _nextLogSequence,
                Terminal = true,
                SanitizedError = sanitizedError,
                CurrentBuild = _currentBuild
            });
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

            _ = QueuePublish(new LlamaCppSourceBuildStatusEvent
            {
                Phase = _phase.ToString(),
                AppendedLogLines = [redacted],
                AppendedLogStartSequence = appendedSequence,
                Terminal = false,
                SanitizedError = null,
                CurrentBuild = _currentBuild
            });
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

    private Task QueuePublish(LlamaCppSourceBuildStatusEvent statusEvent)
    {
        lock (_publishLock)
        {
            _publishTail = PublishObservedAsync(_publishTail, statusEvent);
            return _publishTail;
        }
    }

    private async Task PublishObservedAsync(Task previous, LlamaCppSourceBuildStatusEvent statusEvent)
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

    internal static void ValidateTreeLinks(string rootPath)
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

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FullCommitRegex();
}
