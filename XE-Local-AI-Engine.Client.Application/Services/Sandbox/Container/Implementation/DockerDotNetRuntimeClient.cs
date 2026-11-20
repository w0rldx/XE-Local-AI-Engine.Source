namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;

/// <summary>
///     The production <see cref="IDockerRuntimeClient" />: a thin adapter over <c>Docker.DotNet.Enhanced</c>.
/// </summary>
/// <remarks>
///     Thin on purpose: every decision — what a hardened container is, whether the settings took, which daemon is
///     approved — lives above this class in code a fake client can drive. What lives here is the wire translation
///     and the classification of transport failures into operator-actionable outcomes: a missing socket, a socket
///     that refuses and a daemon that answered are three problems with three fixes, and the daemon does not label
///     them.
/// </remarks>
internal sealed class DockerDotNetRuntimeClient : IContainerRuntime
{
    /// <summary>How long the write probe may take before "the probe did not answer" becomes its answer.</summary>
    internal static readonly TimeSpan WriteProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Captured-output ceiling for the write probe, whose output is a diagnostic and never a result.</summary>
    private const int WriteProbeCaptureBytes = 4 * 1024;

    /// <summary>The per-read buffer for an exec stream. Bounds one read, not the capture; the ceiling does that.</summary>
    private const int ExecReadBufferBytes = 8 * 1024;

    /// <summary>How long stopping a wrapped exec may take before the stop counts as unconfirmed.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a stopped exec's stream may take to reach its end, as the process provider bounds its drain.</summary>
    private static readonly TimeSpan StopOutputDrainWait = TimeSpan.FromSeconds(2);

    /// <summary>How long a stop waits before rerunning its script, or before asking the daemon again whether the wrapper exited.</summary>
    private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The stop script's exit code for "processes of the session were still found after its passes".</summary>
    private const int StopExitStillFound = 1;

    /// <summary>The stop script's exit code for "no identity recorded yet".</summary>
    private const int StopExitNoIdentity = 3;

    /// <summary>The shell functions both exec scripts share; builtins only, so a stop needs no free pid beyond its own shell.</summary>
    /// <remarks>
    ///     <c>__xe_stat</c> reads a whole <c>/proc/&lt;pid&gt;/stat</c> record (a name may hold newlines and <c>") "</c>) and parses state,
    ///     session id and start time after its last <c>") "</c>: 1 when unreadable, 2 when unparseable. <c>__xe_scan</c> sets
    ///     <c>__xe_found</c> for a live process of session <c>$1</c> other than pid <c>$2</c>, or one it cannot parse, and with
    ///     <c>$3</c> set SIGKILLs each one of the session.
    /// </remarks>
    private const string SessionScanFunctions = """
                                                __xe_stat() {
                                                  __xe_l=
                                                  { while IFS= read -r __xe_line || [ -n "$__xe_line" ]; do __xe_l="$__xe_l$__xe_line "; done; } 2>/dev/null < "$1" || return 1
                                                  case "$__xe_l" in *") "*) ;; *) return 2 ;; esac
                                                  set -- ${__xe_l##*) }
                                                  __xe_state=$1 __xe_sid=$4 __xe_start=${20}
                                                  case "$__xe_state" in ?) ;; *) return 2 ;; esac
                                                  case "$__xe_sid" in ''|*[!0-9]*) return 2 ;; esac
                                                  case "$__xe_start" in ''|*[!0-9]*) return 2 ;; esac
                                                }
                                                __xe_scan() {
                                                  __xe_found=
                                                  for __xe_d in /proc/[0-9]*; do
                                                    __xe_stat "$__xe_d/stat"
                                                    case $? in
                                                      1) continue ;;
                                                      2) [ ! -e "$__xe_d" ] || __xe_found=1; continue ;;
                                                    esac
                                                    if [ "$__xe_sid" = "$1" ] && [ "$__xe_state" != Z ] && [ "${__xe_d#/proc/}" != "$2" ]; then
                                                      __xe_found=1
                                                      [ -z "$3" ] || kill -KILL "${__xe_d#/proc/}" 2>/dev/null
                                                    fi
                                                  done
                                                }

                                                """;

    /// <summary>Runs a command so a stop can end it: the identity of the exec's session leader first, then the command as its child.</summary>
    /// <remarks>
    ///     Writes "pid start-time" to the file named by <c>$1</c>; the command is <c>"$2"</c> onwards, positional data, with the exec's
    ///     stdin. A name a builtin shadows runs from the first file of that name on <c>$PATH</c>, its argv[0] then that path (POSIX sh
    ///     cannot set argv[0]); a name it cannot start prints the identity path and why, and the client reports the daemon's words.
    ///     A stop marker refuses a late start; the identity stays while descendants live; deleting it makes a stop time out and kill
    ///     the sandbox. A shebang-less executable text file runs as a sh script, as POSIX sh's ENOEXEC fallback does.
    /// </remarks>
    internal const string StoppableExecScript = SessionScanFunctions + """
                                                                       __xe_f=$1
                                                                       shift
                                                                       __xe_why=
                                                                       __xe_walked=
                                                                       case "$1" in
                                                                         */*)
                                                                           if [ -d "$1" ]; then __xe_why=directory; elif [ ! -e "$1" ]; then __xe_why=missing; elif [ ! -x "$1" ]; then __xe_why=denied; fi
                                                                           ;;
                                                                         *)
                                                                           __xe_x=$(command -v "$1" 2>/dev/null) || __xe_x=
                                                                           case "$__xe_x" in
                                                                             /*) ;;
                                                                             *)
                                                                               __xe_x=$(IFS=:; set -f; for __xe_d in $PATH; do [ -n "$__xe_d" ] || __xe_d=.; if [ -f "$__xe_d/$1" ] && [ -x "$__xe_d/$1" ]; then printf '%s' "$__xe_d/$1"; break; fi; done)
                                                                               __xe_walked=1
                                                                               [ -n "$__xe_x" ] || __xe_why=path
                                                                               ;;
                                                                           esac
                                                                           ;;
                                                                       esac
                                                                       [ -z "$__xe_why" ] || { printf '%s %s\n' "$__xe_f" "$__xe_why" >&2; exit 127; }
                                                                       if __xe_stat "/proc/$$/stat" && printf '%s %s\n' "$$" "$__xe_start" 2>/dev/null > "$__xe_f"; then
                                                                         :
                                                                       else
                                                                         printf '%s\n' 'The sandbox could not record the identity it needs to stop this command.' >&2
                                                                         exit 125
                                                                       fi
                                                                       if [ -e "$__xe_f.stop" ]; then
                                                                         rm -f "$__xe_f" "$__xe_f.stop" 2>/dev/null
                                                                         exit 125
                                                                       fi
                                                                       if [ -n "$__xe_walked" ]; then
                                                                         shift
                                                                         "$__xe_x" "$@"
                                                                       else
                                                                         "$@"
                                                                       fi
                                                                       __xe_r=$?
                                                                       __xe_scan "$$" "$$"
                                                                       [ -n "$__xe_found" ] || rm -f "$__xe_f" 2>/dev/null
                                                                       exit "$__xe_r"
                                                                       """;

    /// <summary>Stops the command whose identity file is <c>$1</c>: marker first, then SIGKILL to its session until a scan finds none.</summary>
    /// <remarks>
    ///     Exit 0 is "its session is empty", 1 still found after 20 passes (the client reruns it), 3 no identity, 4 a leader alive with
    ///     another start time. The command can write the file, so 4 is never read as stopped and its session is not signalled: the pid
    ///     may be another command's. Exit 0 is not confirmation on its own either; the client then accounts for every process left.
    /// </remarks>
    internal const string StopExecScript = SessionScanFunctions + """
                                                                  __xe_f=$1
                                                                  : 2>/dev/null > "$__xe_f.stop"
                                                                  read -r __xe_p __xe_t 2>/dev/null < "$__xe_f" && [ -n "$__xe_t" ] || exit 3
                                                                  if __xe_stat "/proc/$__xe_p/stat" && [ "$__xe_start" != "$__xe_t" ]; then
                                                                    exit 4
                                                                  fi
                                                                  __xe_n=0
                                                                  while :; do
                                                                    __xe_scan "$__xe_p" "" kill
                                                                    [ -n "$__xe_found" ] || break
                                                                    __xe_n=$((__xe_n + 1))
                                                                    [ "$__xe_n" -lt 20 ] || exit 1
                                                                  done
                                                                  rm -f "$__xe_f" "$__xe_f.stop" 2>/dev/null
                                                                  exit 0
                                                                  """;

    private readonly DockerClient _client;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _pullTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Execs this client started whose stream has not ended, by id: the processes a stop leaves alone.</summary>
    private readonly ConcurrentDictionary<string, byte> _liveExecs = new(StringComparer.Ordinal);

    /// <summary>Processes finished commands left running, pid to start time as the daemon lists them: a stop leaves them alone too.</summary>
    private readonly ConcurrentDictionary<long, string> _knownSurvivors = new();

    private int _unrecognisedHealthLogged;


    /// <param name="endpoint">The daemon endpoint this client talks to.</param>
    /// <param name="probeTimeout">Bounds <see cref="ProbeAsync" />, and floors the transport timeout.</param>
    /// <param name="timeProvider">Clock, for the pull-progress aggregator's throttling of downstream reports.</param>
    /// <param name="requestTimeout">
    ///     The HTTP request timeout, null for <paramref name="probeTimeout" />. Separate because a ten-second
    ///     transport sized for a preflight would cut off a thirty-second graceful stop.
    /// </param>
    /// <param name="pullTimeout">
    ///     Deadline for one image pull, applied as a linked cancellation rather than a transport timeout, which must
    ///     not be sized for an image that takes minutes to fetch.
    /// </param>
    /// <param name="logger">
    ///     For two things only: the once-per-client warning about an unrecognised health state, and a debug line
    ///     naming a failed write probe.
    /// </param>
    public DockerDotNetRuntimeClient(DockerDaemonEndpoint endpoint,
        TimeSpan probeTimeout,
        TimeProvider timeProvider,
        TimeSpan? requestTimeout = null,
        TimeSpan? pullTimeout = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _probeTimeout = probeTimeout;
        _pullTimeout = pullTimeout ?? TimeSpan.FromMinutes(30);
        _logger = logger ?? NullLogger.Instance;
        _client = new DockerClientBuilder().WithEndpoint(endpoint.Uri).WithTimeout(requestTimeout ?? probeTimeout).Build();
    }

    public DockerDaemonEndpoint Endpoint { get; }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task<DockerDaemonIdentity> ProbeAsync(CancellationToken cancellationToken = default)
    {
        // A Unix socket that is not there surfaces as AddressNotAvailable, which reads as a networking fault rather
        // than "Docker is not running". Checking the path first names the fix for the platform's commonest failure.
        var socketPath = Endpoint.UnixSocketPath;
        if (socketPath is not null && !File.Exists(socketPath) && !Directory.Exists(socketPath))
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                $"No Docker socket exists at '{socketPath}'.");
        }

        // The Windows twin: a pipe with no listener is retried by the transport until its timeout, so a stopped Docker
        // Desktop would cost the whole probe budget and then read as a timeout rather than "start Docker".
        var pipeName = Endpoint.NamedPipeName;
        if (pipeName is not null && OperatingSystem.IsWindows() && !NamedPipeExists(pipeName))
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                $"No Docker named pipe exists at '{Endpoint.Display}'. Start Docker Desktop.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_probeTimeout);

            await _client.System.PingAsync(timeout.Token);
            var version = await _client.System.GetVersionAsync(timeout.Token);
            var info = await _client.System.GetSystemInfoAsync(timeout.Token);

            if (string.IsNullOrWhiteSpace(info.ID))
            {
                throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                    "The Docker daemon did not report an installation id, so this node cannot pin which daemon it is talking to.");
            }

            return new DockerDaemonIdentity
            {
                DaemonId = info.ID,
                ServerVersion = version.Version ?? string.Empty,
                ApiVersion = version.APIVersion ?? string.Empty,
                MinimumApiVersion = version.MinAPIVersion ?? string.Empty,
                OperatingSystem = version.Os ?? string.Empty,
                Endpoint = Endpoint,
                IsRootless = IsRootless(info),
                SupportsSeccomp = HasSecurityOption(info, "seccomp")
            };
        }
        catch (Exception exception) when (exception is not DockerRuntimeException and not OperationCanceledException)
        {
            throw Classify(exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                $"The Docker daemon at '{Endpoint.Display}' did not answer within {_probeTimeout.TotalSeconds:0} seconds.", exception);
        }
    }

    public async Task<string> CreateContainerAsync(DockerContainerSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var parameters = new CreateContainerParameters
        {
            Image = specification.Image,
            Name = specification.Name,
            User = specification.User,
            WorkingDir = specification.WorkingDirectory,
            Entrypoint = [.. specification.Entrypoint],
            Cmd = [.. specification.Command],
            Labels = specification.Labels.ToDictionary(StringComparer.Ordinal),
            HostConfig = new HostConfig
            {
                NetworkMode = specification.NetworkMode,
                Privileged = false,
                CapDrop = [.. specification.CapabilitiesToDrop],
                CapAdd = [],
                SecurityOpt = [.. specification.SecurityOptions],
                ReadonlyRootfs = specification.ReadOnlyRootFilesystem,
                Tmpfs = specification.TemporaryFilesystems.ToDictionary(StringComparer.Ordinal),
                Mounts = [.. specification.BindMounts.Select(ToMount)],
                Memory = specification.MemoryBytes,
                NanoCPUs = specification.NanoCpus,
                PidsLimit = specification.PidsLimit,
                Devices = [],
                DeviceRequests = [],
                // Left at the daemon's private defaults rather than set to "private": Docker rejects an explicit
                // "private" for PidMode, and the verifier treats an empty mode as private, which is what it is.
                IpcMode = "private",
                AutoRemove = false
            }
        };

        try
        {
            var created = await _client.Containers.CreateContainerAsync(parameters, cancellationToken);
            return created.ID;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<DockerContainerSettings> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            var inspected = await _client.Containers.InspectContainerAsync(containerId, cancellationToken);
            var hostConfig = inspected.HostConfig
                             ?? throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                                 $"The Docker daemon returned no host configuration for container '{containerId}', so its isolation settings cannot be verified.");

            return new DockerContainerSettings
            {
                ContainerId = inspected.ID,
                User = inspected.Config?.User ?? string.Empty,
                NetworkMode = hostConfig.NetworkMode ?? string.Empty,
                Privileged = hostConfig.Privileged,
                ReadOnlyRootFilesystem = hostConfig.ReadonlyRootfs,
                CapabilitiesDropped = hostConfig.CapDrop?.ToArray() ?? [],
                CapabilitiesAdded = hostConfig.CapAdd?.ToArray() ?? [],
                SecurityOptions = hostConfig.SecurityOpt?.ToArray() ?? [],
                TemporaryFilesystems = hostConfig.Tmpfs is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(hostConfig.Tmpfs, StringComparer.Ordinal),
                Mounts = hostConfig.Mounts?.Select(FromMount).ToArray() ?? [],
                MemoryBytes = hostConfig.Memory,
                NanoCpus = hostConfig.NanoCPUs,
                PidsLimit = hostConfig.PidsLimit ?? 0,
                DeviceCount = (hostConfig.Devices?.Count ?? 0) + (hostConfig.DeviceRequests?.Count ?? 0),
                PidMode = hostConfig.PidMode ?? string.Empty,
                IpcMode = hostConfig.IpcMode ?? string.Empty,
                UtsMode = hostConfig.UTSMode ?? string.Empty
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<IReadOnlyList<string>> ListContainersAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            // An empty filter would list every container on the daemon, and the caller removes what this returns.
            throw new ArgumentException("Listing containers without a label filter is refused: the result is used to remove containers.",
                nameof(labels));
        }

        try
        {
            var parameters = new ContainersListParameters
            {
                // Stopped containers too: an engine killed mid-run leaves an exited container behind that still holds
                // its name, so a sweep that only saw running ones would leave the next create failing on the conflict.
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    ["label"] = labels.ToDictionary(pair => pair.Key + "=" + pair.Value, _ => true, StringComparer.Ordinal)
                }
            };

            var listed = await _client.Containers.ListContainersAsync(parameters, cancellationToken);
            return [.. listed.Select(container => container.ID)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            await _client.Containers
                         .RemoveContainerAsync(containerId, new ContainerRemoveParameters
                         {
                             Force = true,
                             RemoveVolumes = true
                         }, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone. Removal is idempotent by contract so that the fail-closed create path can always clean up
            // after itself without having to reason about how far it got.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<DockerExecutionOutcome> ExecuteAsync(string containerId,
        DockerExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(request);

        // A token the client mints, so the path is never caller text.
        var identityPath = request.StopMarkerDirectory is null
            ? null
            : request.StopMarkerDirectory.TrimEnd('/') + "/.xe-exec-" + Guid.NewGuid().ToString("N");
        var outcome = await ExecuteOnceAsync(containerId, request, identityPath, cancellationToken);
        // Answered from the supervised attempt: a second, unwrapped exec could start a file installed in between, unsupervised.
        return identityPath is not null
               && outcome is { Stopped: false, ExitCode: 127, StandardOutput: "" }
               && DescribeLaunchFailureOrNull(outcome.StandardError, identityPath, request.Executable) is { } launchFailure
            ? launchFailure
            : outcome;
    }

    /// <summary>
    ///     The outcome the daemon gives for an exec it cannot start, built from the wrapper's reason line, or null when the standard
    ///     error is not that line.
    /// </summary>
    /// <remarks>
    ///     The text and exit codes are runc's, measured on 2026-11-19 against an unwrapped exec: on standard output, standard error
    ///     empty. The reason line carries the client-minted identity path, which a command cannot know, so no command output passes
    ///     for it.
    /// </remarks>
    internal static DockerExecutionOutcome? DescribeLaunchFailureOrNull(string standardError, string identityPath, string executable)
    {
        var prefix = identityPath + " ";
        if (!standardError.StartsWith(prefix, StringComparison.Ordinal) || !standardError.EndsWith('\n'))
        {
            return null;
        }

        var reason = standardError[prefix.Length..^1] switch
        {
            "path" => "executable file not found in $PATH",
            "missing" => $"stat {executable}: no such file or directory",
            "denied" => "permission denied",
            "directory" => "is a directory: permission denied",
            _ => null
        };

        if (reason is null)
        {
            return null;
        }

        return new DockerExecutionOutcome
        {
            // runc's 126 is "found, cannot run"; 127 is "not found".
            ExitCode = reason.EndsWith("permission denied", StringComparison.Ordinal) ? 126 : 127,
            StandardOutput = $"OCI runtime exec failed: exec failed: unable to start container process: exec: \"{executable}\": {reason}\r\n",
            StandardError = string.Empty,
            StandardOutputTruncated = false,
            StandardErrorTruncated = false
        };
    }

    private async Task<DockerExecutionOutcome> ExecuteOnceAsync(string containerId,
        DockerExecutionRequest request,
        string? identityPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                // Attached only when there is something to send: with stdin attached and nothing written, the child's
                // read blocks until the connection is torn down, so every ordinary command would wait for input.
                AttachStdin = request.StandardInput is not null,
                TTY = false,
                Cmd = identityPath is null
                    ? [request.Executable, .. request.Arguments]
                    : ["/bin/sh", "-c", StoppableExecScript, "xe-exec", identityPath, request.Executable, .. request.Arguments],
                WorkingDir = request.WorkingDirectory ?? string.Empty,
                Env = request.Environment?.Select(pair => pair.Key + "=" + pair.Value).ToArray() ?? []
            };

            var created = await _client.Exec.CreateContainerExecAsync(containerId, parameters, cancellationToken);
            _liveExecs.TryAdd(created.ID, 0);
            try
            {
                MultiplexedStream stream;
                try
                {
                    stream = await _client.Exec
                                          .StartContainerExecAsync(created.ID, new ContainerExecStartParameters
                                          {
                                              Detach = false,
                                              TTY = false
                                          }, cancellationToken);
                }
                catch (OperationCanceledException) when (identityPath is not null && cancellationToken.IsCancellationRequested)
                {
                    // The start may have reached the daemon. The stop leaves a marker that keeps a late start from running the command.
                    var confirmed = await StopAsync(containerId, created.ID, identityPath);
                    return new ExecCapture(request.MaxCapturedBytes, observer: null).ToOutcome(exitCode: -1, stopped: true, confirmed);
                }

                using (stream)
                {
                    var capture = new ExecCapture(request.MaxCapturedBytes, request.OutputObserver);
                    // The read never sees the caller's token: a read cancelled mid-frame would leave the multiplexed framing unreadable,
                    // and the output printed before a stop is still to be drained after it.
                    using var drain = new CancellationTokenSource();
#pragma warning disable CA2025 // The finally below awaits the pump before the stream or the drain source is disposed.
                    var pump = PumpAsync(stream, request.StandardInput, capture, drain.Token);
#pragma warning restore CA2025
                    try
                    {
                        await pump.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !pump.IsCompleted)
                    {
                        var confirmed = identityPath is not null && await StopAsync(containerId, created.ID, identityPath);
                        await SettleAsync(pump, drain, identityPath is null ? TimeSpan.Zero : StopOutputDrainWait);
                        if (identityPath is null)
                        {
                            throw;
                        }

                        return capture.ToOutcome(exitCode: -1, stopped: true, confirmed);
                    }
                    catch (OperationCanceledException) when (pump.IsCompleted)
                    {
                        // The command ended as the cancel landed: it is reported as finished, with its output and exit code.
                    }
                    finally
                    {
                        // WhenAny never throws: it only keeps the stream and the drain source alive until the pump is done.
                        await Task.WhenAny(pump);
                    }

                    await pump;
                    // The stream has ended, so the exit code is final; a cancel arriving now must not relabel a finished command.
                    var inspected = await _client.Exec.InspectContainerExecAsync(created.ID, CancellationToken.None);
                    if (identityPath is not null)
                    {
                        await RecordSurvivorsAsync(containerId, created.ID, inspected.Pid);
                    }

                    return capture.ToOutcome(inspected.ExitCode ?? -1, stopped: false, stopConfirmed: false);
                }
            }
            finally
            {
                _liveExecs.TryRemove(created.ID, out _);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    /// <summary>
    ///     Ends a wrapped exec and every process in its session; true only once its session is empty and the daemon lists no process
    ///     outside the sessions this client accounts for.
    /// </summary>
    /// <remarks>Never throws and ignores the caller's token: it runs because that token was cancelled, and its own deadline bounds it.</remarks>
    private async Task<bool> StopAsync(string containerId, string execId, string identityPath)
    {
        using var deadline = new CancellationTokenSource(StopTimeout, _timeProvider);
        try
        {
            return await ConfirmStopAsync(token => RunStopScriptAsync(containerId, execId, identityPath, token),
                       async token => (await _client.Exec.InspectContainerExecAsync(execId, token)).Running,
                       _timeProvider,
                       deadline.Token)
                   && await OnlyAccountedProcessesRemainAsync(containerId, execId, deadline.Token);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Stop of exec {ExecId} in container {ContainerId} did not complete in time.", execId, containerId);
            return false;
        }
    }

    /// <summary>
    ///     Reruns the stop script while it still finds processes, <see cref="StopPollInterval" /> apart, until the deadline cancels
    ///     the wait: a process in uninterruptible sleep, or one slow to exit, outlives one run's tight passes but not the deadline.
    /// </summary>
    /// <remarks>
    ///     No identity yet means the wrapper had not recorded one, and the marker the stop left makes it exit without running the
    ///     command: confirmed once the daemon reports the exec over. Any other exit code is unconfirmed.
    /// </remarks>
    internal static async Task<bool> ConfirmStopAsync(Func<CancellationToken, Task<long>> runStopScript,
        Func<CancellationToken, Task<bool>> isExecRunning,
        TimeProvider timeProvider,
        CancellationToken deadline)
    {
        while (true)
        {
            var exitCode = await runStopScript(deadline);
            if (exitCode == 0)
            {
                return true;
            }

            if (exitCode == StopExitNoIdentity)
            {
                while (await isExecRunning(deadline))
                {
                    await Task.Delay(StopPollInterval, timeProvider, deadline);
                }

                return true;
            }

            if (exitCode != StopExitStillFound)
            {
                return false;
            }

            await Task.Delay(StopPollInterval, timeProvider, deadline);
        }
    }

    /// <summary>
    ///     True when every live process in the container is in init's session, in another in-flight exec's session, or a known
    ///     survivor, read from daemon records no command in the container can write.
    /// </summary>
    /// <remarks>
    ///     A process cannot join an existing session, only start a new one, so anything of the stopped command still alive, including a
    ///     descendant that left for its own session, is unaccounted and makes the stop unconfirmed. After a confirmed stop nothing is
    ///     left to record as a survivor.
    /// </remarks>
    private async Task<bool> OnlyAccountedProcessesRemainAsync(string containerId, string stoppedExecId, CancellationToken cancellationToken)
    {
        var unaccounted = await ListUnaccountedProcessesAsync(containerId, stoppedExecId, cancellationToken);
        if (unaccounted is null)
        {
            return false;
        }

        if (unaccounted.Count > 0)
        {
            _logger.LogDebug("Stop of exec {ExecId} in container {ContainerId} left processes no known exec accounts for: {Processes}",
                stoppedExecId,
                containerId,
                string.Join("; ", unaccounted.Select(static process => $"{process.Pid} sid {process.Session}")));
        }

        return unaccounted.Count == 0;
    }

    /// <summary>
    ///     Records what a wrapped command that finished left running as known survivors, which a later stop does not count against
    ///     itself. Never throws: a survivor not recorded only makes a later stop unconfirmed.
    /// </summary>
    /// <remarks>
    ///     With another exec in flight, a process in a new session cannot be told apart from a sibling's detached descendant, so only
    ///     the finished command's own session is recorded then; with none in flight, every unaccounted process came from a command
    ///     that has ended.
    /// </remarks>
    private async Task RecordSurvivorsAsync(string containerId, string finishedExecId, long finishedSession)
    {
        using var deadline = new CancellationTokenSource(StopTimeout, _timeProvider);
        try
        {
            if (await ListUnaccountedProcessesAsync(containerId, finishedExecId, deadline.Token) is { } processes)
            {
                // Read after the list: the finished exec is still tracked, so more than one means another exec is in flight.
                var othersInFlight = _liveExecs.Count > 1;
                foreach (var process in processes.Where(process => !othersInFlight || process.Session == finishedSession))
                {
                    _knownSurvivors[process.Pid] = process.Started;
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The processes exec {ExecId} left in container {ContainerId} could not be recorded.", finishedExecId, containerId);
        }
    }

    /// <summary>
    ///     The daemon's live processes in the container outside init's session, the sessions of in-flight execs other than
    ///     <paramref name="excludedExecId" />, and the known survivors; null when the list is unusable.
    /// </summary>
    /// <remarks>
    ///     The daemon reports exec and init pids in the namespace its process list uses; an exec leader is its session's id. The tracked
    ///     set is read before and after the list, so an exec ending in between is still counted.
    /// </remarks>
    private async Task<IReadOnlyList<ContainerProcess>?> ListUnaccountedProcessesAsync(string containerId,
        string excludedExecId,
        CancellationToken cancellationToken)
    {
        var tracked = _liveExecs.Keys.ToHashSet(StringComparer.Ordinal);
        var listed = await _client.Containers.ListProcessesAsync(containerId,
            new ContainerListProcessesParameters
            {
                // The start time goes last: the daemon keeps only the last column's spaces.
                PsArgs = "-e -o pid,sid,stat,lstart"
            },
            cancellationToken);
        tracked.UnionWith(_liveExecs.Keys);
        tracked.Remove(excludedExecId);

        // An init the daemon does not report accounts for nothing, so its processes then count as unaccounted.
        var sessions = new HashSet<long>
        {
            (await _client.Containers.InspectContainerAsync(containerId, cancellationToken)).State?.Pid ?? 0
        };
        foreach (var execId in tracked)
        {
            sessions.Add((await _client.Exec.InspectContainerExecAsync(execId, cancellationToken)).Pid);
        }

        var unaccounted = SelectUnaccounted(listed.Titles, listed.Processes, sessions, _knownSurvivors);
        if (unaccounted is null)
        {
            _logger.LogDebug("Process list of container {ContainerId} lacks a usable column: {Titles}", containerId, string.Join(' ', listed.Titles));
        }

        return unaccounted;
    }

    /// <summary>
    ///     The live rows outside <paramref name="sessions" /> that are not a known survivor, null when a column is missing or a row
    ///     unparseable. Prunes every known survivor the list does not show with the same start time.
    /// </summary>
    /// <remarks>
    ///     A survivor is matched by pid and start time, so a recycled pid is not trusted. The start time has a one-second grain: a pid
    ///     reused within the same second as the survivor's start would pass for it.
    /// </remarks>
    internal static IReadOnlyList<ContainerProcess>? SelectUnaccounted(IList<string> titles,
        IList<IList<string>> rows,
        IReadOnlySet<long> sessions,
        ConcurrentDictionary<long, string> knownSurvivors)
    {
        int[] columns = [titles.IndexOf("PID"), titles.IndexOf("SID"), titles.IndexOf("STAT"), titles.IndexOf("STARTED")];
        if (columns.Any(static column => column < 0))
        {
            return null;
        }

        var live = new List<ContainerProcess>();
        foreach (var row in rows)
        {
            if (row.Count < titles.Count
                || !long.TryParse(row[columns[0]], CultureInfo.InvariantCulture, out var pid)
                || !long.TryParse(row[columns[1]], CultureInfo.InvariantCulture, out var session))
            {
                return null;
            }

            if (!row[columns[2]].StartsWith('Z'))
            {
                live.Add(new ContainerProcess
                {
                    Pid = pid,
                    Session = session,
                    Started = row[columns[3]]
                });
            }
        }

        foreach (var gone in knownSurvivors.Where(survivor =>
                     !live.Any(process => process.Pid == survivor.Key && string.Equals(process.Started, survivor.Value, StringComparison.Ordinal))))
        {
            knownSurvivors.TryRemove(gone);
        }

        return live.Where(process => !sessions.Contains(process.Session)
                                     && !(knownSurvivors.TryGetValue(process.Pid, out var started) && string.Equals(started, process.Started, StringComparison.Ordinal)))
                   .ToArray();
    }

    /// <summary>One run of <see cref="StopExecScript" /> in its own exec; its exit code.</summary>
    private async Task<long> RunStopScriptAsync(string containerId, string execId, string identityPath, CancellationToken cancellationToken)
    {
        var created = await _client.Exec.CreateContainerExecAsync(containerId,
            new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                AttachStdin = false,
                TTY = false,
                Cmd = ["/bin/sh", "-c", StopExecScript, "xe-stop", identityPath],
                WorkingDir = string.Empty,
                Env = []
            },
            cancellationToken);
        var capture = new ExecCapture(WriteProbeCaptureBytes, observer: null);
        long exitCode;
        // Tracked too: a stop of one command runs while a sibling's stop may be signalling its own session.
        _liveExecs.TryAdd(created.ID, 0);
        try
        {
            using (var stream = await _client.Exec.StartContainerExecAsync(created.ID,
                       new ContainerExecStartParameters
                       {
                           Detach = false,
                           TTY = false
                       },
                       cancellationToken))
            {
                await PumpAsync(stream, standardInput: null, capture, cancellationToken);
            }

            exitCode = (await _client.Exec.InspectContainerExecAsync(created.ID, cancellationToken)).ExitCode ?? -1;
        }
        finally
        {
            _liveExecs.TryRemove(created.ID, out _);
        }

        if (exitCode != 0)
        {
            _logger.LogDebug("Stop of exec {ExecId} in container {ContainerId} exited {ExitCode}: {Output}",
                execId,
                containerId,
                exitCode,
                capture.StandardOutput + capture.StandardError);
        }

        return exitCode;
    }

    /// <summary>Lets a pump reach the end of its stream for at most <paramref name="wait" />, then cuts it; the capture keeps what was read.</summary>
    private async Task SettleAsync(Task pump, CancellationTokenSource drain, TimeSpan wait)
    {
        using (var delay = new CancellationTokenSource())
        {
            await Task.WhenAny(pump, Task.Delay(wait, _timeProvider, delay.Token));
            await delay.CancelAsync();
        }

        await drain.CancelAsync();
        await Task.WhenAny(pump);
    }

    // IContainerRuntime — the application-container surface. Everything below is additive to the members above, so
    // a Development Mode create still produces byte-identical wire parameters.

    public async Task<string> RunContainerAsync(ContainerSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        // Guards first, before a parameter is built: `required` means assigned, not non-empty, and a blank host IP
        // renders as a binding the daemon resolves to 0.0.0.0. No such container may exist for a read-back to catch.
        if (specification.PublishedPorts.FirstOrDefault(publication => !IsLoopback(publication.HostIp)) is { } offender)
        {
            throw new ArgumentException($"Container port {offender.ContainerPort}/{offender.Protocol} asks to publish on host interface "
                                        + $"'{offender.HostIp}'. Application containers publish on 127.0.0.1 and on nothing else.",
                nameof(specification));
        }

        if (!ContainerImageReference.IsContentAddressed(specification.Image))
        {
            throw new ArgumentException($"Image '{specification.Image}' is not digest-pinned. A tag names whatever the registry last pushed, "
                                        + "not the bytes the catalog approved; give a 'name@sha256:<digest>' reference or a bare image id.",
                nameof(specification));
        }

        if (specification.User is not null && string.IsNullOrWhiteSpace(specification.User))
        {
            throw new ArgumentException("A blank container user is refused. Pass null for the image's own default user; \"\" and null would "
                                        + "otherwise be the same instruction written two ways and only one of them says what it means.",
                nameof(specification));
        }

        // Container port plus protocol keys both wire dictionaries below, so two publications sharing one throw the
        // BCL's own duplicate-key error outside the try: unclassified, and naming neither port nor specification.
        if (specification.PublishedPorts.GroupBy(PortKey, StringComparer.Ordinal)
                         .FirstOrDefault(group => group.Skip(1).Any()) is { } duplicated)
        {
            throw new ArgumentException($"Container port {duplicated.Key} is published more than once. A container port and protocol name one "
                                        + "exposed port on the daemon, so the second publication would replace the first rather than add to "
                                        + "it. Publish each container port once, on one host port.",
                nameof(specification));
        }

        var parameters = new CreateContainerParameters
        {
            Image = specification.Image,
            Name = specification.Name,
            Labels = specification.Labels.ToDictionary(StringComparer.Ordinal),
            Env = [.. specification.Environment.Select(pair => pair.Key + "=" + pair.Value)],
            ExposedPorts = specification.PublishedPorts.ToDictionary(PortKey, _ => default(EmptyStruct), StringComparer.Ordinal),
            // Exactly one endpoint: the daemon rejects a create that names more than one network.
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>(StringComparer.Ordinal)
                {
                    [specification.NetworkName] = new()
                    {
                        Aliases = [.. specification.NetworkAliases]
                    }
                }
            },
            HostConfig = new HostConfig
            {
                NetworkMode = specification.NetworkName,
                Privileged = false,
                CapDrop = [.. specification.CapabilitiesToDrop],
                CapAdd = [.. specification.CapabilitiesToAdd],
                SecurityOpt = [.. specification.SecurityOptions],
                ReadonlyRootfs = specification.ReadOnlyRootFilesystem,
                Mounts = [.. specification.Mounts.Select(ToMount)],
                PortBindings = specification.PublishedPorts.ToDictionary(PortKey,
                    publication => (IList<PortBinding>)
                    [
                        new PortBinding
                        {
                            HostIP = publication.HostIp,
                            // The empty string, never "0", is how the daemon is asked to assign a port: "0" is a
                            // request to bind port zero.
                            HostPort = publication.HostPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
                        }
                    ],
                    StringComparer.Ordinal),
                PublishAllPorts = false,
                ExtraHosts = [.. specification.ExtraHosts],
                RestartPolicy = new RestartPolicy
                {
                    Name = specification.RestartMode == ContainerRestartMode.UnlessStopped
                        ? RestartPolicyKind.UnlessStopped
                        : RestartPolicyKind.No
                },
                Memory = specification.MemoryBytes,
                NanoCPUs = specification.NanoCpus,
                PidsLimit = specification.PidsLimit,
                // Unconditional, and not conditional on a manifest flag: ADR 0010 makes GPU device requests a
                // non-goal, so there is no path through this method that can ask for one.
                Devices = [],
                DeviceRequests = [],
                IpcMode = "private",
                AutoRemove = false
            }
        };

        if (specification.User is not null)
        {
            parameters.User = specification.User;
        }

        if (specification.Entrypoint is not null)
        {
            parameters.Entrypoint = [.. specification.Entrypoint];
        }

        if (specification.Command is not null)
        {
            parameters.Cmd = [.. specification.Command];
        }

        if (specification.WorkingDirectory is not null)
        {
            parameters.WorkingDir = specification.WorkingDirectory;
        }

        if (specification.Healthcheck is { } healthcheck)
        {
            parameters.Healthcheck = new HealthcheckConfig
            {
                Test = [.. healthcheck.Test],
                Interval = healthcheck.Interval,
                Timeout = healthcheck.Timeout,
                Retries = healthcheck.Retries,
                StartPeriod = healthcheck.StartPeriod
            };
        }

        try
        {
            var created = await _client.Containers.CreateContainerAsync(parameters, cancellationToken);
            return created.ID;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<ContainerInspection> InspectAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            var inspected = await _client.Containers.InspectContainerAsync(containerId, cancellationToken);
            return ToInspection(containerId, inspected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DockerRuntimeException)
        {
            throw Classify(exception);
        }
    }

    /// <summary>
    ///     The inspect wire mapping, separated from the call so it can be driven without a daemon. Internal as a test
    ///     seam via <c>InternalsVisibleTo</c>; not part of the public contract.
    /// </summary>
    internal ContainerInspection ToInspection(string containerId, ContainerInspectResponse inspected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(inspected);

        var hostConfig = inspected.HostConfig
                         ?? throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                             $"The Docker daemon returned no host configuration for container '{containerId}', so its settings cannot be verified.");

        return new ContainerInspection
        {
            ContainerId = inspected.ID,
            // The daemon reports the name with a leading slash; the caller compares it against the name it asked
            // for, and a comparison that fails on a punctuation mark is not a verification.
            Name = (inspected.Name ?? string.Empty).TrimStart('/'),
            // Config.Image, not the top-level Image, which is the resolved ID: the caller verifies the container was
            // created from the reference it pinned, and the fake seam answers with that same reference.
            Image = inspected.Config?.Image ?? string.Empty,
            Labels = inspected.Config?.Labels is { } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            State = ToRunState(inspected.State),
            RequestedPortBindings = ToRequestedPortBindings(hostConfig.PortBindings),
            PublishedPorts = ToPublishedPorts(inspected.NetworkSettings?.Ports),
            User = inspected.Config?.User ?? string.Empty,
            NetworkMode = hostConfig.NetworkMode ?? string.Empty,
            Privileged = hostConfig.Privileged,
            ReadOnlyRootFilesystem = hostConfig.ReadonlyRootfs,
            CapabilitiesDropped = hostConfig.CapDrop?.ToArray() ?? [],
            CapabilitiesAdded = hostConfig.CapAdd?.ToArray() ?? [],
            SecurityOptions = hostConfig.SecurityOpt?.ToArray() ?? [],
            // The TOP-LEVEL Mounts, not HostConfig.Mounts: only the effective set carries an anonymous volume the
            // image's own VOLUME instruction created, the one mount that silently escapes the instance directory.
            Mounts = inspected.Mounts?.Select(ToMountView).ToArray() ?? [],
            DeviceCount = (hostConfig.Devices?.Count ?? 0) + (hostConfig.DeviceRequests?.Count ?? 0),
            PidMode = hostConfig.PidMode ?? string.Empty,
            IpcMode = hostConfig.IpcMode ?? string.Empty,
            UtsMode = hostConfig.UTSMode ?? string.Empty,
            MemoryBytes = hostConfig.Memory,
            NanoCpus = hostConfig.NanoCPUs,
            PidsLimit = hostConfig.PidsLimit ?? 0,
            RestartMode = hostConfig.RestartPolicy?.Name == RestartPolicyKind.UnlessStopped
                ? ContainerRestartMode.UnlessStopped
                : ContainerRestartMode.None,
            ExtraHosts = hostConfig.ExtraHosts?.ToArray() ?? []
        };
    }

    public async Task<bool> StopContainerAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            return await _client.Containers
                                .StopContainerAsync(containerId,
                                    new ContainerStopParameters
                                    {
                                        WaitBeforeKillSeconds = (uint)Math.Clamp(gracePeriod.TotalSeconds, 0, 600)
                                    },
                                    cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone is the same answer as "it was not running": false, not an error, matching the idempotence
            // RemoveContainerAsync promises, so a teardown never reasons about how far a previous attempt got.
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<bool> ImageExistsAsync(string imageReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        try
        {
            await _client.Images.InspectImageAsync(imageReference, cancellationToken);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
        catch (DockerApiException apiException) when (apiException.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task PullImageAsync(string imageReference,
        IProgress<ContainerPullProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var separator = imageReference.IndexOf("@sha256:", StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ArgumentException($"Image '{imageReference}' is not digest-pinned. This layer never pulls a tag: a tag names whatever "
                                        + "the registry last pushed, not the bytes the catalog approved.",
                nameof(imageReference));
        }

        var aggregator = new PullProgressAggregator(imageReference, _timeProvider);
        var sink = new PullProgressSink(aggregator, progress);

        // Its own deadline rather than the transport's: an application image is large, and a pull sized by the
        // ten-second timeout a preflight needs would fail on every first install.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_pullTimeout);

        try
        {
            await _client.Images
                         .CreateImageAsync(new ImagesCreateParameters
                             {
                                 FromImage = imageReference[..separator],
                                 Tag = imageReference[(separator + 1)..]
                             },
                             authConfig: null,
                             sink,
                             deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                $"The pull of '{imageReference}' did not complete within {_pullTimeout.TotalMinutes:0} minutes.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }

        // A pull that failed part-way still completes the HTTP call normally and reports the failure inside the
        // stream, so this read is the difference between a named failure and a silent half-pull.
        if (aggregator.Error is { } error)
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                $"The pull of '{imageReference}' failed: {error}");
        }

        progress?.Report(aggregator.Snapshot());
    }

    public async Task<string> CreateNetworkAsync(ContainerNetworkSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (string.IsNullOrWhiteSpace(specification.Name))
        {
            throw new ArgumentException("A container network specification must carry a name.", nameof(specification));
        }

        // Before the create, not after the conflict: the labels ARE the ownership proof network reuse rests on, and
        // with none OwnsNetwork has nothing to compare, so a foreign bridge holding the name would pass.
        if (specification.Labels.Count == 0)
        {
            throw new ArgumentException($"The container network '{specification.Name}' carries no labels. At least one ownership label is "
                                        + "required: a network is reused only when it provably belongs to this engine, and an empty label map "
                                        + "makes that check vacuous.",
                nameof(specification));
        }

        try
        {
            return await CreateNetworkOnceAsync(specification, cancellationToken);
        }
        catch (DockerApiException apiException) when (apiException.StatusCode == HttpStatusCode.Conflict)
        {
            // A name conflict says some network holds that name, not that it is ours, and a name is not a capability:
            // attaching to a bridge somebody else created shares it with everything else on it. Reuse needs proof.
            NetworkResponse existing;
            try
            {
                existing = await _client.Networks.InspectNetworkAsync(specification.Name, cancellationToken);
            }
            catch (DockerNetworkNotFoundException)
            {
                // Removed in the gap between the conflict and the inspect. One retry, then it fails classified.
                try
                {
                    return await CreateNetworkOnceAsync(specification, cancellationToken);
                }
                catch (Exception retryFailure) when (retryFailure is not OperationCanceledException)
                {
                    throw Classify(retryFailure);
                }
            }
            catch (Exception inspectFailure) when (inspectFailure is not OperationCanceledException)
            {
                throw Classify(inspectFailure);
            }

            if (!OwnsNetwork(existing, specification))
            {
                throw new ContainerPolicyException(ContainerPolicyException.ForeignNetworkReason,
                    $"The network name '{specification.Name}' is in use by a foreign container network "
                    + $"(id '{existing.ID}', driver '{existing.Driver}'). It was not created by this application "
                    + "instance, so it is not reused.", apiException);
            }

            return existing.ID;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task RemoveNetworkAsync(string networkNameOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkNameOrId);

        try
        {
            await _client.Networks.DeleteNetworkAsync(networkNameOrId, cancellationToken);
        }
        catch (DockerNetworkNotFoundException)
        {
            // Already gone, and that is the outcome the caller wanted. A network that still has containers attached
            // is a different matter: it fails classified, which is what makes teardown ordering observable.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<IReadOnlyList<string>> ListNetworksAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            throw new ArgumentException("Listing networks without a label filter is refused: the result is used to remove networks.",
                nameof(labels));
        }

        try
        {
            var listed = await _client.Networks
                                      .ListNetworksAsync(new NetworksListParameters
                                      {
                                          Filters = LabelFilter(labels)
                                      }, cancellationToken);

            return [.. listed.Select(network => network.ID)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    // The daemon's state vocabulary is lower-case and this value is compared against those literals rather than
    // displayed, so upper-casing would normalize away from the wire format the comparison is against.
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Docker's container state words are lower-case on the wire and are compared, not displayed.")]
    public async Task<IReadOnlyList<ContainerSummary>> ListContainersDetailedAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            throw new ArgumentException("Listing containers without a label filter is refused: the result drives lifecycle decisions.",
                nameof(labels));
        }

        try
        {
            var listed = await _client.Containers
                                      .ListContainersAsync(new ContainersListParameters
                                      {
                                          // The default lists running containers only, so one the user stopped would
                                          // look removed, and the observer would report a crash as gone.
                                          All = true,
                                          Filters = LabelFilter(labels)
                                      }, cancellationToken);

            return
            [
                .. listed.Select(container => new ContainerSummary
                {
                    Id = container.ID,
                    Labels = container.Labels is { } containerLabels
                        ? new Dictionary<string, string>(containerLabels, StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal),
                    State = (container.State ?? string.Empty).ToLowerInvariant(),
                    ExitCode = ParseExitCode(container.Status)
                })
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<ContainerLogSnapshot> ReadLogsAsync(string containerId,
        ContainerLogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var stream = await _client.Containers
                                            .GetContainerLogsAsync(containerId,
                                                new ContainerLogsParameters
                                                {
                                                    ShowStdout = true,
                                                    ShowStderr = true,
                                                    Timestamps = false,
                                                    Follow = false,
                                                    // Both are strings on the wire, neither a number nor a DateTime,
                                                    // so both are formatted invariantly, not by the node's locale.
                                                    Tail = request.TailLines.ToString(CultureInfo.InvariantCulture),
                                                    Since = request.SinceUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                                                },
                                                cancellationToken);

            var buffer = new byte[16 * 1024];
            var kept = new List<byte>(Math.Min(request.MaxBytes, 64 * 1024));
            var truncated = false;

            // Containers are created with TTY unset, so the daemon always frames the stream and it is demultiplexed
            // into one builder in the order the daemon framed it, which is the order things happened.
            while (true)
            {
                var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                if (read.EOF)
                {
                    break;
                }

                if (read.Count == 0)
                {
                    continue;
                }

                var room = request.MaxBytes - kept.Count;
                if (read.Count > room)
                {
                    // Strictly greater, so a read landing exactly on the ceiling is not reported as truncated — the
                    // next read decides, and "truncated" on a complete log sends a caller after bytes never dropped.
                    kept.AddRange(buffer.AsSpan(0, room));
                    truncated = true;
                    break;
                }

                kept.AddRange(buffer.AsSpan(0, read.Count));
            }

            // Encoding.UTF8 decodes with a replacement fallback, so a ceiling that cut through a multi-byte sequence
            // yields U+FFFD rather than throwing: a bounded read is a policy, not a data-integrity failure.
            var text = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(kept));

            return new ContainerLogSnapshot
            {
                Text = text,
                Truncated = truncated,
                LineCount = text.AsSpan().Count('\n')
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<bool> ProbeWritablePathAsync(string containerId, string containerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(WriteProbeTimeout);

        try
        {
            var parameters = new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                AttachStdin = false,
                TTY = false,
                // The path travels as $1, so a container path holding a space or a quote is data, not shell syntax.
                // The command is fixed, so no consumer of this layer ever chooses what runs inside a container.
                Cmd = ["sh", "-c", "f=\"$1/.xe-write-probe-$$\"; : > \"$f\" && rm -f \"$f\"", "_", containerPath],
                WorkingDir = string.Empty,
                Env = []
            };

            var created = await _client.Exec.CreateContainerExecAsync(containerId, parameters, deadline.Token);
            using var stream = await _client.Exec
                                            .StartContainerExecAsync(created.ID,
                                                new ContainerExecStartParameters
                                                {
                                                    Detach = false,
                                                    TTY = false
                                                },
                                                deadline.Token);

            var captured = new ExecCapture(WriteProbeCaptureBytes, observer: null);
            await PumpAsync(stream, standardInput: null, captured, deadline.Token);
            var inspected = await _client.Exec.InspectContainerExecAsync(created.ID, deadline.Token);
            var exitCode = inspected.ExitCode ?? -1;

            if (exitCode != 0)
            {
                var output = captured.StandardOutput + captured.StandardError;
                _logger.LogDebug("Write probe of '{ContainerPath}' in container {ContainerId} exited {ExitCode}: {Output}",
                    containerPath,
                    containerId,
                    exitCode,
                    output);
            }

            return exitCode == 0;
        }
        catch (DockerContainerNotFoundException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // "The probe did not answer" is the same install-blocking answer as "the mount is not writable", so it is
            // returned rather than thrown: a caller that had to catch a timeout here would have two ways to say no.
            _logger.LogDebug("Write probe of '{ContainerPath}' in container {ContainerId} did not answer within {Seconds} seconds.",
                containerPath,
                containerId,
                WriteProbeTimeout.TotalSeconds);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    /// <summary>The one loopback literal, compared ordinally: a host IP is a wire value, not localized text.</summary>
    /// <remarks>
    ///     <c>::1</c> is deliberately NOT accepted. A published port is reached by this node's own processes and by
    ///     the operator's browser, both given a <c>127.0.0.1</c> address, so a daemon asked to publish on the IPv6
    ///     loopback binds a socket nothing here connects to — a port that reads back as published and answers
    ///     nobody. One address is also one thing to reserve and one thing to compare a policy check against.
    /// </remarks>
    private static bool IsLoopback(string hostIp)
    {
        return string.Equals(hostIp, "127.0.0.1", StringComparison.Ordinal);
    }

    private static string PortKey(ContainerPortPublication publication)
    {
        return publication.ContainerPort.ToString(CultureInfo.InvariantCulture) + "/" + publication.Protocol;
    }

    /// <summary>
    ///     Both dictionary levels are constructed explicitly. <c>Filters</c> is interface-typed and defaults to null on
    ///     4.3.3, so the nested collection-initializer form dereferences null at run time rather than failing to
    ///     compile.
    /// </summary>
    private static Dictionary<string, IDictionary<string, bool>> LabelFilter(IReadOnlyDictionary<string, string> labels)
    {
        var byLabel = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, value) in labels)
        {
            byLabel[key + "=" + value] = true;
        }

        return new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["label"] = byLabel
        };
    }

    private async Task<string> CreateNetworkOnceAsync(ContainerNetworkSpecification specification, CancellationToken cancellationToken)
    {
        var created = await _client.Networks
                                   .CreateNetworkAsync(new NetworksCreateParameters
                                   {
                                       Name = specification.Name,
                                       Driver = "bridge",
                                       Internal = specification.Internal,
                                       Attachable = false,
                                       EnableIPv6 = false,
                                       // Assigned rather than initialized into: Labels is interface-typed and null by
                                       // default. CheckDuplicate is deliberately absent — it does not exist in 4.3.3.
                                       Labels = new Dictionary<string, string>(specification.Labels, StringComparer.Ordinal)
                                   }, cancellationToken);

        return created.ID;
    }

    /// <summary>
    ///     Whether an existing network of the same name is provably this specification's own: the <c>bridge</c>
    ///     driver, and every label the specification asked for present with the same value.
    /// </summary>
    /// <remarks>
    ///     The instance and install ids the caller puts in that label map are therefore a security input, not
    ///     bookkeeping.
    /// </remarks>
    private static bool OwnsNetwork(NetworkResponse existing, ContainerNetworkSpecification specification)
    {
        if (!string.Equals(existing.Driver, "bridge", StringComparison.Ordinal))
        {
            return false;
        }

        if (existing.Labels is not { } labels)
        {
            // An unlabelled network can never carry this specification's labels, and CreateNetworkAsync refuses a
            // specification that has none — so there is no "both empty" case that could make this true.
            return false;
        }

        foreach (var (key, value) in specification.Labels)
        {
            if (!labels.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private ContainerRunState ToRunState(State? state)
    {
        return new ContainerRunState
        {
            Running = state?.Running ?? false,
            Status = state?.Status ?? string.Empty,
            ExitCode = state?.ExitCode ?? 0,
            OutOfMemoryKilled = state?.OOMKilled ?? false,
            Health = ToHealthState(state?.Health?.Status),
            StartedAtUtc = ParseTimestamp(state?.StartedAt),
            FinishedAtUtc = ParseTimestamp(state?.FinishedAt)
        };
    }

    /// <summary>Maps the daemon's health word.</summary>
    /// <remarks>
    ///     A null status means no healthcheck was declared and is silent; an unrecognised status is
    ///     <see cref="ContainerHealthState.None" /> plus one warning carrying the value, emitted once per client so a
    ///     renamed daemon state is visible. The caller reads <c>None</c> from a service that declared a healthcheck
    ///     as "not yet healthy", so an unrecognised word delays to the deadline rather than passing as healthy.
    /// </remarks>
    // Internal rather than private so a unit test can pin the mapping and the once-per-client warning: the only
    // other way in is InspectAsync, which needs a daemon that can be asked to report a state it does not have.
    internal ContainerHealthState ToHealthState(string? status)
    {
        if (status is null)
        {
            return ContainerHealthState.None;
        }

        if (status.Equals("starting", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthState.Starting;
        }

        if (status.Equals("healthy", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthState.Healthy;
        }

        if (status.Equals("unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthState.Unhealthy;
        }

        if (Interlocked.Exchange(ref _unrecognisedHealthLogged, 1) == 0)
        {
            _logger.LogWarning("The Docker daemon reported container health state '{HealthStatus}', which this engine does not "
                               + "recognise. It is treated as not yet healthy.",
                status);
        }

        return ContainerHealthState.None;
    }

    /// <summary>
    ///     The daemon renders these as RFC 3339 strings and uses <c>0001-01-01T00:00:00Z</c> for "never", which must
    ///     read as null: a caller shown a start time of year one would render it rather than omit it.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return parsed == default ? null : parsed;
    }

    /// <summary>
    ///     What the daemon was <em>asked</em> to bind. Present from create onwards, and a blank host port maps to null
    ///     rather than dropping the entry: "daemon-assigned" is a request the pre-start check has to see.
    /// </summary>
    private static IReadOnlyList<ContainerPortPublication> ToRequestedPortBindings(IDictionary<string, IList<PortBinding>>? bindings)
    {
        if (bindings is null)
        {
            return [];
        }

        var requested = new List<ContainerPortPublication>();
        foreach (var (key, bound) in bindings)
        {
            if (!TryParsePortKey(key, out var containerPort, out var protocol) || bound is null)
            {
                continue;
            }

            foreach (var binding in bound)
            {
                requested.Add(new ContainerPortPublication
                {
                    ContainerPort = containerPort,
                    Protocol = protocol,
                    HostIp = binding.HostIP ?? string.Empty,
                    HostPort = int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort)
                        ? hostPort
                        : null
                });
            }
        }

        return requested;
    }

    /// <summary>
    ///     What the daemon actually bound. Empty before start, which is why the request side exists; an entry whose
    ///     host port is blank is not a published port and is skipped.
    /// </summary>
    private static IReadOnlyList<ContainerPublishedPort> ToPublishedPorts(IDictionary<string, IList<PortBinding>>? ports)
    {
        if (ports is null)
        {
            return [];
        }

        var published = new List<ContainerPublishedPort>();
        foreach (var (key, bound) in ports)
        {
            if (!TryParsePortKey(key, out var containerPort, out var protocol) || bound is null)
            {
                continue;
            }

            foreach (var binding in bound)
            {
                if (!int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort))
                {
                    continue;
                }

                published.Add(new ContainerPublishedPort
                {
                    ContainerPort = containerPort,
                    Protocol = protocol,
                    HostIp = binding.HostIP ?? string.Empty,
                    HostPort = hostPort
                });
            }
        }

        return published;
    }

    private static bool TryParsePortKey(string key, out int containerPort, [NotNullWhen(true)] out string? protocol)
    {
        var separator = key.IndexOf('/', StringComparison.Ordinal);
        var portText = separator < 0 ? key : key[..separator];
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out containerPort))
        {
            protocol = null;
            return false;
        }

        protocol = separator < 0 ? "tcp" : key[(separator + 1)..];
        return true;
    }

    /// <summary>
    ///     The exit code of a listed container, read out of the daemon's own status prose, the list response carrying
    ///     no exit-code member.
    /// </summary>
    /// <remarks>
    ///     Null on any other shape, and null means "the daemon did not say" — never <c>0</c>, which would report a
    ///     crashed application as a clean exit.
    /// </remarks>
    // Internal rather than private so a unit test can pin the shapes: this reads a number out of daemon prose, and
    // the real-daemon test proves the prose while this proves the parser.
    internal static int? ParseExitCode(string? status)
    {
        if (string.IsNullOrEmpty(status) || !status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var open = status.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        var close = status.IndexOf(')', open + 1);
        if (close < 0)
        {
            return null;
        }

        return int.TryParse(status.AsSpan(open + 1, close - open - 1),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var exitCode)
            ? exitCode
            : null;
    }

    private static Mount ToMount(ContainerMount mount)
    {
        return new Mount
        {
            Type = "bind",
            Source = mount.HostPath,
            Target = mount.ContainerPath,
            ReadOnly = mount.ReadOnly,
            BindOptions = new BindOptions
            {
                Propagation = mount.Propagation
            }
        };
    }

    private static ContainerMountView ToMountView(MountPoint mount)
    {
        return new ContainerMountView
        {
            Type = mount.Type ?? string.Empty,
            Source = mount.Source ?? string.Empty,
            Destination = mount.Destination ?? string.Empty,
            // RW is the daemon's own word for the effective set and is non-nullable here, so read-only is its negation
            // rather than a separate absent-means-writable rule.
            ReadOnly = !mount.RW
        };
    }

    /// <summary>
    ///     Feeds the daemon's message stream through the aggregator and relays only what the throttle lets out.
    /// </summary>
    /// <remarks>
    ///     A hand-written sink rather than <see cref="Progress{T}" />, which posts to a synchronization context and
    ///     would reorder or defer the reports relative to the pull that produced them.
    /// </remarks>
    private sealed class PullProgressSink : IProgress<JSONMessage>
    {
        private readonly PullProgressAggregator _aggregator;
        private readonly IProgress<ContainerPullProgress>? _downstream;

        public PullProgressSink(PullProgressAggregator aggregator, IProgress<ContainerPullProgress>? downstream)
        {
            _aggregator = aggregator;
            _downstream = downstream;
        }

        public void Report(JSONMessage value)
        {
            // Folded even when nobody is listening: the aggregator is also where the stream's error is captured, and
            // a caller that passed no progress still needs the pull to fail rather than half-succeed.
            var progress = _aggregator.Report(value);
            if (progress is not null)
            {
                _downstream?.Report(progress);
            }
        }
    }

    /// <summary>Drives both directions of one exec stream into <paramref name="capture" />.</summary>
    /// <remarks>
    ///     The payload goes up WHILE the output is drained: a Docker exec is one bidirectional connection, so with
    ///     nothing reading, a child that writes as it reads fills the daemon's buffer, the daemon stops accepting the
    ///     payload and both sides wait until the command times out — Development Mode's 8 MB patch ceiling is well
    ///     past where a serialized version stops working. The half-close after the write is equally load-bearing: a
    ///     child reading to end-of-input never returns while the write side is open.
    /// </remarks>
    private static async Task PumpAsync(MultiplexedStream stream,
        string? standardInput,
        ExecCapture capture,
        CancellationToken cancellationToken)
    {
        if (standardInput is null)
        {
            await ReadBoundedAsync(stream, capture, cancellationToken);
            return;
        }

        var payload = Encoding.UTF8.GetBytes(standardInput);
        var writeTask = SendAsync(stream, payload, cancellationToken);
        try
        {
            await ReadBoundedAsync(stream, capture, cancellationToken);
        }
        finally
        {
            // Awaited inside this method, so the stream outlives both halves: a write still in flight when the caller
            // disposed the stream would be a use-after-dispose rather than a tidy-up detail.
            await writeTask;
        }
    }

    /// <summary>
    ///     Drain an exec stream to its end into <paramref name="capture" />, which keeps at most its ceiling of each half
    ///     and discards the rest as it arrives.
    /// </summary>
    /// <remarks>
    ///     The ceiling is applied DURING the read, because the other end is a process inside an image the engine did
    ///     not build and <c>ReadOutputToEndAsync</c> accumulates everything before anyone truncates it, letting a
    ///     container that writes a gigabyte decide how much of this node's memory it uses. The stream is still
    ///     drained to the end: the caller's deadline bounds the time, and stopping early leaves the daemon writing
    ///     into a connection nobody reads. The capture outlives a cancelled read, so what arrived before it is kept.
    /// </remarks>
    internal static async Task ReadBoundedAsync(MultiplexedStream stream, ExecCapture capture, CancellationToken cancellationToken)
    {
        var buffer = new byte[ExecReadBufferBytes];
        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, offset: 0, buffer.Length, cancellationToken);
            if (read.EOF)
            {
                break;
            }

            capture.Append(read.Target == MultiplexedStream.TargetStream.StandardError, buffer, read.Count);
        }

        capture.Complete();
    }

    private static async Task SendAsync(MultiplexedStream stream, byte[] payload, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(payload, offset: 0, payload.Length, cancellationToken);
        stream.CloseWrite();
    }

    /// <summary>Whether the daemon runs rootless, read off the same <c>SecurityOptions</c> list <c>docker info</c> prints.</summary>
    private static bool IsRootless(SystemInfoResponse info)
    {
        return HasSecurityOption(info, "rootless");
    }

    /// <summary>
    ///     Whether <c>docker info</c> lists <paramref name="name" /> among the daemon's security options.
    /// </summary>
    /// <remarks>
    ///     Matched on the <c>name=</c> key rather than the whole entry, because the daemon renders these as
    ///     comma-separated key/value groups and only the name is stable.
    /// </remarks>
    private static bool HasSecurityOption(SystemInfoResponse info, string name)
    {
        return info.SecurityOptions?.Any(option => option
                                                   .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                                   .Any(part => part.Equals("name=" + name, StringComparison.OrdinalIgnoreCase)))
               ?? false;
    }

    private static Mount ToMount(DockerBindMount bindMount)
    {
        return new Mount
        {
            Type = "bind",
            Source = bindMount.HostPath,
            Target = bindMount.ContainerPath,
            ReadOnly = bindMount.ReadOnly,
            BindOptions = new BindOptions
            {
                Propagation = bindMount.Propagation
            }
        };
    }

    private static DockerBindMount FromMount(Mount mount)
    {
        return new DockerBindMount
        {
            HostPath = mount.Source ?? string.Empty,
            ContainerPath = mount.Target ?? string.Empty,
            // A read-write mount comes back with ReadOnly absent rather than false, so null has to read as "writable".
            // Read as "unknown" instead, it would fail the read-only check on every ordinary workspace mount.
            ReadOnly = mount.ReadOnly ?? false,
            Propagation = mount.BindOptions?.Propagation ?? string.Empty
        };
    }

    /// <summary>One row of the daemon's process list for a container, in the daemon's pid namespace.</summary>
    internal sealed record ContainerProcess
    {
        public required long Pid { get; init; }

        public required long Session { get; init; }

        /// <summary>The start time as the daemon's <c>ps</c> prints it, to the second.</summary>
        public required string Started { get; init; }
    }

    /// <summary>Both captured streams of one exec, each bounded by the caller's per-stream ceiling, plus the line observer.</summary>
    internal sealed class ExecCapture
    {
        private readonly BoundedCapture _standardError;
        private readonly SandboxOutputLineSplitter? _standardErrorLines;
        private readonly BoundedCapture _standardOutput;
        private readonly SandboxOutputLineSplitter? _standardOutputLines;

        public ExecCapture(int maxBytesPerStream, ISandboxOutputObserver? observer)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerStream);
            _standardOutput = new BoundedCapture(maxBytesPerStream);
            _standardError = new BoundedCapture(maxBytesPerStream);
            _standardOutputLines = observer is null ? null : new SandboxOutputLineSplitter(observer, SandboxOutputSource.StandardOutput);
            _standardErrorLines = observer is null ? null : new SandboxOutputLineSplitter(observer, SandboxOutputSource.StandardError);
        }

        public string StandardOutput => _standardOutput.Text;

        public string StandardError => _standardError.Text;

        public bool StandardOutputTruncated => _standardOutput.Truncated;

        public bool StandardErrorTruncated => _standardError.Truncated;

        public void Append(bool isError, byte[] buffer, int count)
        {
            (isError ? _standardError : _standardOutput).Append(buffer, count);
            (isError ? _standardErrorLines : _standardOutputLines)?.Write(buffer.AsSpan(0, count));
        }

        public void Complete()
        {
            _standardOutputLines?.Complete();
            _standardErrorLines?.Complete();
        }

        public DockerExecutionOutcome ToOutcome(long exitCode, bool stopped, bool stopConfirmed)
        {
            return new DockerExecutionOutcome
            {
                ExitCode = exitCode,
                StandardOutput = _standardOutput.Text,
                StandardError = _standardError.Text,
                StandardOutputTruncated = StandardOutputTruncated,
                StandardErrorTruncated = StandardErrorTruncated,
                Stopped = stopped,
                StopConfirmed = stopConfirmed
            };
        }
    }

    /// <summary>
    ///     One captured stream, bounded: the first <c>maxBytes</c> bytes are kept and everything after them is
    ///     counted as dropped and discarded rather than buffered.
    /// </summary>
    private sealed class BoundedCapture
    {
        private readonly byte[] _kept;
        private int _length;

        public BoundedCapture(int maxBytes)
        {
            _kept = new byte[maxBytes];
        }

        public bool Truncated { get; private set; }

        // Decoded with the replacement fallback so a ceiling that cuts through a multi-byte sequence yields a
        // replacement character rather than throwing: a bounded capture is a policy, not a data-integrity claim.
        public string Text => Encoding.UTF8.GetString(_kept, index: 0, _length);

        public void Append(byte[] source, int count)
        {
            var copied = Math.Min(_kept.Length - _length, count);
            if (copied > 0)
            {
                Array.Copy(source, sourceIndex: 0, _kept, _length, copied);
                _length += copied;
            }

            if (copied < count)
            {
                Truncated = true;
            }
        }
    }

    /// <summary>Classify a transport or API failure into the operator-actionable outcome behind it.</summary>
    /// <remarks>
    ///     The socket error code is the load-bearing signal: <c>AccessDenied</c> means the socket is there and this
    ///     process may not use it, a permissions fix, whereas every other connect failure means nothing is listening,
    ///     a "start the daemon" fix. A <see cref="TimeoutException" /> is the named-pipe transport's word for the same:
    ///     no listener answered its connect. Matching on daemon prose instead would break on the next Docker release.
    /// </remarks>
    internal DockerRuntimeException Classify(Exception exception)
    {
        var socketException = FindInChain<SocketException>(exception);
        if (socketException is not null)
        {
            return socketException.SocketErrorCode == SocketError.AccessDenied
                ? new DockerRuntimeException(DockerDaemonPreflightStatus.PermissionDenied,
                    $"Access to the Docker endpoint '{Endpoint.Display}' was denied.", exception)
                : new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                    $"The Docker endpoint '{Endpoint.Display}' could not be reached ({socketException.SocketErrorCode}).", exception);
        }

        if (FindInChain<TimeoutException>(exception) is not null)
        {
            return new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                $"The Docker endpoint '{Endpoint.Display}' did not answer in time.", exception);
        }

        if (exception is DockerApiException apiException)
        {
            return new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                $"The Docker daemon at '{Endpoint.Display}' rejected the request with {apiException.StatusCode}.", exception);
        }

        if (exception is HttpRequestException or IOException)
        {
            return new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                $"The Docker endpoint '{Endpoint.Display}' could not be reached.", exception);
        }

        return new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
            $"The Docker daemon at '{Endpoint.Display}' could not be used: {exception.Message}", exception);
    }

    /// <summary>Whether the local pipe namespace lists <paramref name="pipeName" />; true when it cannot be read.</summary>
    /// <remarks>
    ///     Enumerating <c>\\.\pipe\</c> lists names without connecting. <see cref="File.Exists(string)" /> would open an
    ///     instance, which the daemon sees as a client and which reports a pipe whose instances are all busy as missing.
    /// </remarks>
    internal static bool NamedPipeExists(string pipeName)
    {
        const string pipeRoot = @"\\.\pipe\";
        try
        {
            return Directory.EnumerateFiles(pipeRoot).Contains(pipeRoot + pipeName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Cannot tell, so do not claim absence: the transport's own connect decides.
            return true;
        }
    }

    private static T? FindInChain<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    var found = FindInChain<T>(inner);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }
}
