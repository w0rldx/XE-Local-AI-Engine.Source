namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Bounded Linux toolchain checks, run before a managed native-runtime source build is allowed to start.
/// </summary>
/// <remarks>
///     Every probe is a real invocation of the tool, because "is it on PATH" and "does it run" differ often enough to
///     matter — a CUDA toolkit whose <c>nvcc</c> cannot load its own libraries is on PATH and still cannot build.
///     Each one is capped in output and in time, runs under the same scrubbed environment as the build itself, and is
///     killed if it overruns, so a wedged tool costs one bounded wait rather than the operator's whole request.
///     The row order is part of the contract: the SPA renders the checklist in the order it arrives.
/// </remarks>
public abstract class SourceBuildPrerequisiteProbe
{
    private const int MaxProbeOutputChars = 4096;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    // The one free-disk measurement in the node; see DriveInfoFreeSpaceProbe for why a path root is the wrong input.
    private static readonly IFreeSpaceProbe FreeSpace = new DriveInfoFreeSpaceProbe();
    private readonly string _cacheRoot;
    private readonly long _requiredFreeDiskBytes;
    private readonly bool _requiresReadelf;
    private readonly string _probeIsolationRoot;

    /// <param name="cacheRoot">The runtime cache root the build writes under.</param>
    /// <param name="requiredFreeDiskBytes">The free space the cache root's mount must have.</param>
    /// <param name="runtimeDirectoryName">The runtime's directory under the cache root.</param>
    /// <param name="requiresReadelf">Whether the runtime's post-build gate runs <c>readelf</c>.</param>
    protected SourceBuildPrerequisiteProbe(string cacheRoot, long requiredFreeDiskBytes, string runtimeDirectoryName, bool requiresReadelf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _requiredFreeDiskBytes = requiredFreeDiskBytes;
        _requiresReadelf = requiresReadelf;
        _probeIsolationRoot = Path.Combine(cacheRoot, runtimeDirectoryName, "source-build", ".probe");
    }

    /// <summary>The whole checklist, in the order the SPA shows it.</summary>
    /// <param name="probeBackendTools">Probes the accelerator rows the requested backend needs, shown right after the OS gate.</param>
    /// <param name="ct">Cancels the probe and kills the tool it is running.</param>
    protected async Task<IReadOnlyList<SourceBuildPrerequisiteRow>> ProbeRowsAsync(Func<CancellationToken, Task<IReadOnlyList<SourceBuildPrerequisiteRow>>> probeBackendTools,
        CancellationToken ct)
    {
        // The OS gate is reported as the single unsatisfied row rather than an exception, so the SPA renders one
        // checklist shape everywhere and a Windows operator is told why the lane is unavailable.
        if (!OperatingSystem.IsLinux())
        {
            return [Row("os-is-linux", satisfied: false, "In-app source builds are available on Linux only.")];
        }

        var rows = new List<SourceBuildPrerequisiteRow>
        {
            Row("os-is-linux", satisfied: true, "Linux host detected.")
        };
        rows.AddRange(await probeBackendTools(ct).ConfigureAwait(false));
        rows.Add(await ProbeToolAsync("cmake", ["--version"], "CMake", ct).ConfigureAwait(false));
        rows.Add(await ProbeToolAsync("gcc", ["--version"], "C compiler (gcc)", ct).ConfigureAwait(false));
        rows.Add(await ProbeToolAsync("g++", ["--version"], "C++ compiler (g++)", ct).ConfigureAwait(false));
        rows.Add(await ProbeEitherToolAsync(ct).ConfigureAwait(false));
        rows.Add(await ProbeToolAsync("git", ["--version"], "git", ct).ConfigureAwait(false));

        // Without readelf a post-build relocation gate fails a build after up to two hours of compiling, on a check the
        // operator could have satisfied in seconds; the gate is backend-independent, so the row is too.
        if (_requiresReadelf)
        {
            rows.Add(await ProbeToolAsync("readelf", ["--version"], "readelf (binutils)", ct).ConfigureAwait(false));
        }

        rows.Add(ProbeFreeDisk());
        return rows;
    }

    /// <summary>The CUDA compiler and driver rows both runtimes' CUDA backends need.</summary>
    protected async Task<IReadOnlyList<SourceBuildPrerequisiteRow>> ProbeCudaToolsAsync(CancellationToken ct)
    {
        return
        [
            await ProbeToolAsync("nvcc", ["--version"], "NVIDIA CUDA compiler (nvcc)", ct, CudaToolkitLocator.FindNvccOutsidePath()).ConfigureAwait(false),
            await ProbeToolAsync("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], "NVIDIA driver probe", ct).ConfigureAwait(false)
        ];
    }

    /// <summary>One tool row, probed under this runtime's isolation root.</summary>
    protected Task<SourceBuildPrerequisiteRow> ProbeToolAsync(string fileName,
        IReadOnlyList<string> arguments,
        string displayName,
        CancellationToken ct,
        string? executablePath = null)
    {
        return ProbeToolAsync(fileName, arguments, displayName, _probeIsolationRoot, ct, executablePath);
    }

    internal static Task<SourceBuildPrerequisiteRow> ProbeToolForTestsAsync(string fileName,
        IReadOnlyList<string> arguments,
        string displayName,
        CancellationToken ct,
        string? isolationRoot = null)
    {
        isolationRoot ??= Path.Combine(Path.GetTempPath(),
            "xe-local-ai-engine",
            "source-probe-tests",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return ProbeToolAsync(fileName, arguments, displayName, isolationRoot, ct);
    }

    // Either build driver satisfies the same requirement, so they share one row: reporting "ninja missing" on a machine
    // that builds fine with make would be a false blocker.
    private async Task<SourceBuildPrerequisiteRow> ProbeEitherToolAsync(CancellationToken ct)
    {
        var ninja = await ProbeToolAsync("ninja", ["--version"], "Ninja", ct).ConfigureAwait(false);
        if (ninja.Satisfied)
        {
            return ninja with
            {
                Key = "make-or-ninja"
            };
        }

        var make = await ProbeToolAsync("make", ["--version"], "Make", ct).ConfigureAwait(false);
        return make with
        {
            Key = "make-or-ninja",
            Detail = make.Satisfied ? make.Detail : "Neither Ninja nor Make is available."
        };
    }

    private SourceBuildPrerequisiteRow ProbeFreeDisk()
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);

            var available = FreeSpace.GetAvailableFreeBytes(_cacheRoot);
            return available >= _requiredFreeDiskBytes
                ? Row("free-disk", satisfied: true, "Sufficient free disk space detected.")
                : Row("free-disk", satisfied: false, "At least 15 GiB of free disk space is required.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Row("free-disk", satisfied: false, "Free disk space could not be verified.");
        }
    }

    // `executablePath`, when given, is the absolute program to spawn instead of resolving `fileName` on PATH — the
    // checklist key and the operator-facing detail stay the bare tool name, so no absolute path is ever surfaced.
    private static async Task<SourceBuildPrerequisiteRow> ProbeToolAsync(string fileName,
        IReadOnlyList<string> arguments,
        string displayName,
        string isolationRoot,
        CancellationToken ct,
        string? executablePath = null)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath ?? fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            SourceProcessHardening.Configure(process.StartInfo, isolationRoot);
            if (!process.Start())
            {
                return Missing(fileName, displayName);
            }

            SourceProcessHardening.CloseStandardInput(process);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);

                // The first line of a --version banner is the most useful thing the operator can be shown, and some
                // tools write it to stderr.
                var firstLine = (output + Environment.NewLine + error)
                                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .FirstOrDefault();
                return process.ExitCode == 0
                    ? Row(fileName, satisfied: true, firstLine ?? $"{displayName} detected.")
                    : Missing(fileName, displayName);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ProcessTreeKill.TryKill(process);
                await IgnoreCancellationAsync(outputTask).ConfigureAwait(false);
                await IgnoreCancellationAsync(errorTask).ConfigureAwait(false);
                return Row(fileName, satisfied: false, $"{displayName} probe timed out.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ProcessTreeKill.TryKill(process);
                await IgnoreCancellationAsync(outputTask).ConfigureAwait(false);
                await IgnoreCancellationAsync(errorTask).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
        {
            return Missing(fileName, displayName);
        }
    }

    private static SourceBuildPrerequisiteRow Row(string key, bool satisfied, string detail)
    {
        return new SourceBuildPrerequisiteRow
        {
            Key = key,
            Satisfied = satisfied,
            Detail = detail
        };
    }

    private static SourceBuildPrerequisiteRow Missing(string key, string displayName)
    {
        return Row(key, satisfied: false, $"{displayName} is not available.");
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The probe process was killed after a timeout or caller cancellation.
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var output = new StringBuilder(MaxProbeOutputChars);
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToString();
            }

            var remaining = MaxProbeOutputChars - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer.AsSpan(0, Math.Min(read, remaining)));
            }
        }
    }
}
