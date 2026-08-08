namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ICudaBuildService" />. Orchestrates a single-flight, cancellable, background in-app CUDA
///     <c>llama-server</c> build and adopts the result as a managed runtime. Every subprocess runs under a scrubbed,
///     allowlisted environment (<c>[secHIGH-2]</c>) in an owner-only (0700) work directory inside the cache root — never
///     <c>/tmp</c> (<c>[secHIGH-3]</c>, Locked #8). The clone source URL + tag are constants and the checked-out commit is
///     verified == the pinned SHA before any cmake runs (<c>[secHIGH-1]</c>). On any failure the partial tree is deleted
///     and nothing is recorded (no silent CPU fallback).
/// </summary>
public sealed partial class CudaBuildService : ICudaBuildService, IDisposable
{
    private const string SourceOwnerRepo = "ggml-org/llama.cpp";
    private const string ManagedFitParamsFileName = "llama-fit-params";
    private const string ManagedServerFileName = "llama-server";

    // Built by interpolation rather than a const literal absolute URI (the source URL+repo are fixed constants).
    private static string SourceUrl => $"https://github.com/{SourceOwnerRepo}";

    // Conservative fallback compute-architecture set when nvidia-smi's compute_cap can't be read/validated. [secMED-1]
    private const string DefaultCudaArchitectures = "75;86;89;120";

    // -j cap: parallel build jobs are min(nproc, this) to bound peak memory/CPU during the build. [secMED-5]
    private const int MaxBuildJobs = 8;

    // The number of streamed log lines retained for the status GET (the hub streams every line live).
    private const int LogRingCapacity = 400;

    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(120);

    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly string _cacheRoot;
    private readonly string _homeDirectory;
    private readonly ILogger<CudaBuildService> _logger;
    private readonly ICudaBuildPrerequisiteProbe _prerequisiteProbe;
    private readonly ICudaBuildEventPublisher _publisher;
    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _buildCts;
    private DateTimeOffset? _completedAtUtc;
    private string? _currentTag;
    private bool _isRunning;
    private List<string> _logLines = [];
    private CudaBuildPhase _phase = CudaBuildPhase.Idle;
    private string? _sanitizedError;
    private DateTimeOffset? _startedAtUtc;

    /// <summary>Creates the build service over the prerequisite probe, the binary manager, and the build-event publisher.</summary>
    public CudaBuildService(ICudaBuildPrerequisiteProbe prerequisiteProbe,
        ILlamaCppBinaryManager binaryManager,
        ICudaBuildEventPublisher publisher,
        ILogger<CudaBuildService> logger)
        : this(prerequisiteProbe, binaryManager, publisher, logger, DefaultCacheRoot())
    {
    }

    /// <summary>Test seam: pins the cache root the build tree lives under.</summary>
    internal CudaBuildService(ICudaBuildPrerequisiteProbe prerequisiteProbe,
        ILlamaCppBinaryManager binaryManager,
        ICudaBuildEventPublisher publisher,
        ILogger<CudaBuildService> logger,
        string cacheRoot)
    {
        _prerequisiteProbe = prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _homeDirectory = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
    }

    private string SourceCudaRoot => Path.Combine(_cacheRoot, "llama.cpp", "source-cuda");

    private string WorkDirectory => Path.Combine(SourceCudaRoot, ".work");

    private string MarkerPath => Path.Combine(WorkDirectory, ".build-in-progress");

    public void Dispose()
    {
        _buildCts?.Dispose();
    }

    /// <inheritdoc />
    public async Task<CudaBuildStartOutcome> StartAsync(CancellationToken ct)
    {
        // Single-flight: a second start while a build is in flight is a no-op that returns AlreadyRunning.
        lock (_stateLock)
        {
            if (_isRunning)
            {
                return CudaBuildStartOutcome.AlreadyRunning;
            }
        }

        // Startup-edge safety: clear a stale work dir from a prior crash/kill before allowing a new build. [archLOW-1]
        RecoverStaleWorkDirectory();

        // Re-check Linux + every prerequisite + free disk BEFORE spawning anything. A failed re-check throws WITHOUT
        // spawning a clone/cmake. [secMED-5] The endpoint enforces the same gates; this is the defense-in-depth re-check.
        if (!OperatingSystem.IsLinux())
        {
            throw new LlamaRuntimeException("The in-app CUDA build is available on Linux only.");
        }

        var report = await _prerequisiteProbe.ProbeAsync(ct).ConfigureAwait(false);
        if (!report.CanBuild)
        {
            throw new LlamaRuntimeException("One or more build prerequisites are missing; resolve the checklist before building.");
        }

        // Claim the single-flight slot and reset status under the lock.
        var buildCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        lock (_stateLock)
        {
            if (_isRunning)
            {
                buildCts.Dispose();
                return CudaBuildStartOutcome.AlreadyRunning;
            }

            _isRunning = true;
            _phase = CudaBuildPhase.Cloning;
            _logLines = [];
            _sanitizedError = null;
            _currentTag = LlamaCppReleasePins.PinnedTag;
            _startedAtUtc = DateTimeOffset.UtcNow;
            _completedAtUtc = null;
            _buildCts?.Dispose();
            _buildCts = buildCts;
        }

        // The build runs detached from the request lifetime: it owns its OWN cancellation token (driven by Cancel()), so
        // the HTTP request that started it can return immediately without aborting the build. The Linux guard here lets the
        // platform analyzer see that the Linux-only build body is never reached off Linux (StartAsync already threw above).
        _ = Task.Run(async () =>
        {
            if (OperatingSystem.IsLinux())
            {
                await RunBuildAsync(buildCts.Token).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        return CudaBuildStartOutcome.Started;
    }

    /// <inheritdoc />
    public CudaBuildStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new CudaBuildStatus(_phase,
                _isRunning,
                Terminal: _phase is CudaBuildPhase.Completed or CudaBuildPhase.Cancelled or CudaBuildPhase.Failed,
                LogLines: [.. _logLines],
                _sanitizedError,
                _currentTag,
                _startedAtUtc,
                _completedAtUtc);
        }
    }

    /// <inheritdoc />
    public bool Cancel()
    {
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            if (!_isRunning || _buildCts is null)
            {
                return false;
            }

            cts = _buildCts;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void RecoverStaleWorkDirectory()
    {
        try
        {
            if (File.Exists(MarkerPath) || Directory.Exists(WorkDirectory))
            {
                TryDeleteDirectory(WorkDirectory);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to clean a stale CUDA build work directory at startup.");
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task RunBuildAsync(CancellationToken ct)
    {
        var sourceCudaRoot = SourceCudaRoot;
        var workDir = WorkDirectory;
        var cloneDir = Path.Combine(workDir, "llama.cpp");
        var buildDir = Path.Combine(cloneDir, "build");
        var tag = LlamaCppReleasePins.PinnedTag;
        var finalTagDir = Path.Combine(sourceCudaRoot, tag);
        // Sibling staging + backup dirs (same filesystem as finalTagDir → moves are atomic). The new build is validated in
        // staging and the previous runtime is parked in backup, so a FAILED rebuild never loses a working runtime.
        var stagingTagDir = Path.Combine(sourceCudaRoot, $".staging-{tag}");
        var backupTagDir = Path.Combine(sourceCudaRoot, $".backup-{tag}");

        try
        {
            // Owner-only (0700) work dir inside the cache root — never /tmp. [secHIGH-3, Locked #8]
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(stagingTagDir);
            TryDeleteDirectory(backupTagDir);
            CreateOwnerOnlyDirectory(sourceCudaRoot);
            CreateOwnerOnlyDirectory(workDir);
            await File.WriteAllTextAsync(MarkerPath, DateTimeOffset.UtcNow.ToString("O"), ct).ConfigureAwait(false);

            var environment = BuildScrubbedEnvironment();

            // 1. Clone the pinned source at the pinned tag (NO submodules; URL+tag are constants).
            SetPhase(CudaBuildPhase.Cloning);
            var cloneExit = await RunStreamingStepAsync("git",
                ["clone", "--depth", "1", "--branch", tag, SourceUrl, cloneDir],
                environment,
                workDir,
                CloneTimeout,
                ct).ConfigureAwait(false);
            if (cloneExit != 0)
            {
                throw new LlamaRuntimeException("Cloning the llama.cpp source failed.");
            }

            // 2. Verify the checked-out commit == the pinned SHA BEFORE any cmake runs. [secHIGH-1]
            SetPhase(CudaBuildPhase.Verifying);
            var (revExit, revOutput) = await RunCaptureAsync("git",
                ["-C", cloneDir, "rev-parse", "HEAD"],
                environment,
                workDir,
                ShortCommandTimeout,
                ct).ConfigureAwait(false);
            var checkedOut = revOutput.Trim();
            if (revExit != 0 || !string.Equals(checkedOut, LlamaCppReleasePins.PinnedCudaSourceCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new LlamaRuntimeException("The cloned source did not match the pinned commit; the build was aborted before configuring.");
            }

            AppendLog($"Verified source commit {checkedOut}.");

            // 3. Detect + validate the compute architecture. [secMED-1]
            var architectures = await ResolveCudaArchitecturesAsync(environment, workDir, ct).ConfigureAwait(false);
            AppendLog($"Building for CUDA architectures: {architectures}.");

            // 4. cmake configure. CMAKE_BUILD_RPATH_USE_ORIGIN is load-bearing: the build tree is produced under a work
            // directory and then PLACED at its final managed path, so an absolute build RUNPATH would point at a
            // directory that no longer exists and llama-server would die at startup with "libllama-server-impl.so:
            // cannot open shared object file" even though the .so sits right beside it. $ORIGIN keeps the placed tree
            // self-referential. Mirrors LlamaCppSourceBuildService, which owns the live path today.
            SetPhase(CudaBuildPhase.Configuring);
            var configureExit = await RunStreamingStepAsync("cmake",
                [
                    "-B", buildDir, "-S", cloneDir, "-DGGML_CUDA=ON", "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_BUILD_RPATH_USE_ORIGIN=ON", "-DLLAMA_CURL=OFF",
                    $"-DCMAKE_CUDA_ARCHITECTURES={architectures}"
                ],
                environment,
                cloneDir,
                ConfigureTimeout,
                ct).ConfigureAwait(false);
            if (configureExit != 0)
            {
                throw new LlamaRuntimeException("Configuring the CUDA build failed.");
            }

            // 5. cmake build (jobs capped). [secMED-5]
            SetPhase(CudaBuildPhase.Building);
            var jobs = Math.Max(1, Math.Min(Environment.ProcessorCount, MaxBuildJobs));
            var buildExit = await RunStreamingStepAsync("cmake",
                ["--build", buildDir, "--target", ManagedServerFileName, ManagedFitParamsFileName, "-j", jobs.ToString()],
                environment,
                cloneDir,
                BuildTimeout,
                ct).ConfigureAwait(false);
            if (buildExit != 0)
            {
                throw new LlamaRuntimeException("Compiling the CUDA llama.cpp runtime failed.");
            }

            // 6. Stage the built tree, harden it, then swap it into place — the previous runtime is only removed AFTER the
            //    new build validates + adopts, so a failed rebuild leaves the working managed runtime untouched.
            SetPhase(CudaBuildPhase.Adopting);
            var builtBin = Path.Combine(buildDir, "bin");
            if (!File.Exists(Path.Combine(builtBin, ManagedServerFileName)))
            {
                throw new LlamaRuntimeException("The build did not produce the expected server executable.");
            }

            if (!File.Exists(Path.Combine(builtBin, ManagedFitParamsFileName)))
            {
                throw new LlamaRuntimeException("The build did not produce the expected fit-params helper.");
            }

            // Stage the placed tree at a sibling dir (same filesystem → atomic moves) and harden it there.
            var stagingBuildDir = Path.Combine(stagingTagDir, "build");
            CreateOwnerOnlyDirectory(stagingTagDir);
            Directory.Move(buildDir, stagingBuildDir);
            HardenTree(sourceCudaRoot, stagingTagDir);

            // Swap into place last: park any previous runtime in the backup, move the staged tree in, then validate + adopt
            // the FINAL binary. On any failure, roll the previous runtime back so the working runtime is never lost.
            var hadPrevious = Directory.Exists(finalTagDir);
            if (hadPrevious)
            {
                Directory.Move(finalTagDir, backupTagDir);
            }

            try
            {
                Directory.Move(stagingTagDir, finalTagDir);
                var finalBin = Path.Combine(finalTagDir, "build", "bin");
                await _binaryManager.AdoptCudaSourceBuildAsync(finalBin, tag, ct).ConfigureAwait(false);
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
            SetTerminal(CudaBuildPhase.Completed, sanitizedError: null);
        }
        catch (OperationCanceledException)
        {
            // The swap step already rolled finalTagDir back on failure; only the partial work + staging trees are dropped.
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(stagingTagDir);
            SetTerminal(CudaBuildPhase.Cancelled, sanitizedError: null);
        }
        catch (LlamaRuntimeException exception)
        {
            _logger.LogWarning(exception, "The in-app CUDA build failed.");
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(stagingTagDir);
            SetTerminal(CudaBuildPhase.Failed, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The in-app CUDA build failed unexpectedly.");
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(stagingTagDir);
            SetTerminal(CudaBuildPhase.Failed, "The CUDA build failed unexpectedly.");
        }
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

            // e.g. "8.9\n8.9" → "89;89"; strip the dot per line, join with ';'.
            var arches = output
                         .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(static line => line.Replace(".", string.Empty, StringComparison.Ordinal))
                         .Where(static value => value.Length > 0);
            var joined = string.Join(';', arches);

            // Whitelist the FINAL string before it reaches -DCMAKE_CUDA_ARCHITECTURES. A non-match → default set.
            return ComputeCapRegex().IsMatch(joined) ? joined : DefaultCudaArchitectures;
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
    private static Dictionary<string, string> BuildScrubbedEnvironment()
    {
        string[] allowlist = ["PATH", "HOME", "TMPDIR", "LANG", "LC_ALL", "CUDA_HOME", "CUDA_PATH"];
        var scrubbed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in allowlist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                scrubbed[key] = value;
            }
        }

        return scrubbed;
    }

    private void SetPhase(CudaBuildPhase phase)
    {
        lock (_stateLock)
        {
            _phase = phase;
        }

        PublishSafe(new CudaBuildStatusHubEvent(phase.ToString(), [], Terminal: false, SanitizedError: null));
    }

    private void SetTerminal(CudaBuildPhase phase, string? sanitizedError)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _isRunning = false;
            _sanitizedError = sanitizedError;
            _completedAtUtc = DateTimeOffset.UtcNow;
        }

        PublishSafe(new CudaBuildStatusHubEvent(phase.ToString(), [], Terminal: true, sanitizedError));
    }

    // The streaming log sink: redact the cache-root/HOME prefix [secLOW-1], retain a bounded ring buffer for the status
    // GET, and push the line live to the hub. Thread-safe (both pipes call this concurrently).
    private void AppendLog(string line)
    {
        var redacted = Redact(line);
        lock (_stateLock)
        {
            _logLines.Add(redacted);
            if (_logLines.Count > LogRingCapacity)
            {
                _logLines.RemoveRange(0, _logLines.Count - LogRingCapacity);
            }
        }

        var phase = GetPhaseSnapshot();
        PublishSafe(new CudaBuildStatusHubEvent(phase.ToString(), [redacted], Terminal: false, SanitizedError: null));
    }

    private CudaBuildPhase GetPhaseSnapshot()
    {
        lock (_stateLock)
        {
            return _phase;
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

    private void PublishSafe(CudaBuildStatusHubEvent statusEvent)
    {
        try
        {
            _ = _publisher.PublishStatusAsync(statusEvent, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Publishing a CUDA build status event failed (non-fatal).");
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

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine");
    }

    [GeneratedRegex(@"^[0-9]{2,3}(;[0-9]{2,3})*$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ComputeCapRegex();
}
