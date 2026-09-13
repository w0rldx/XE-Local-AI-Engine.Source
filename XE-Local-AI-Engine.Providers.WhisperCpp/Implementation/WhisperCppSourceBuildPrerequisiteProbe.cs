namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Bounded Linux toolchain checks, run before a managed whisper.cpp build is allowed to start.
/// </summary>
/// <remarks>
///     Every probe is a real invocation of the tool, because "is it on PATH" and "does it run" differ often enough to
///     matter — a CUDA toolkit whose <c>nvcc</c> cannot load its own libraries is on PATH and still cannot build.
///     Each one is capped in output and in time, runs under the same scrubbed environment as the build itself, and is
///     killed if it overruns, so a wedged tool costs one bounded wait rather than the operator's whole request.
/// </remarks>
public sealed class WhisperCppSourceBuildPrerequisiteProbe : IWhisperCppSourceBuildPrerequisiteProbe
{
    private const int MaxProbeOutputChars = 4096;

    /// <summary>A whisper.cpp CUDA build's source, objects and installed tree, with room to spare.</summary>
    internal const long RequiredFreeDiskBytes = 15L * 1024 * 1024 * 1024;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private readonly string _cacheRoot;
    private readonly long _requiredFreeDiskBytes;

    public WhisperCppSourceBuildPrerequisiteProbe()
        : this(DefaultCacheRoot(), RequiredFreeDiskBytes)
    {
    }

    internal WhisperCppSourceBuildPrerequisiteProbe(string cacheRoot, long requiredFreeDiskBytes = RequiredFreeDiskBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _requiredFreeDiskBytes = requiredFreeDiskBytes;
    }

    private string ProbeIsolationRoot => Path.Combine(_cacheRoot, "whisper.cpp", "source-build", ".probe");

    /// <inheritdoc />
    public async Task<WhisperCppSourceBuildPrerequisiteReport> ProbeAsync(WhisperBackend backend, CancellationToken ct)
    {
        // The OS gate is reported as the single unsatisfied row rather than an exception, so the SPA renders one
        // checklist shape everywhere and a Windows operator is told why the lane is unavailable.
        if (!OperatingSystem.IsLinux())
        {
            return new WhisperCppSourceBuildPrerequisiteReport(false,
            [
                new WhisperCppSourceBuildPrerequisiteItem("os-is-linux", false, "In-app source builds are available on Linux only.")
            ]);
        }

        var items = new List<WhisperCppSourceBuildPrerequisiteItem>
        {
            new("os-is-linux", true, "Linux host detected."),
            await ProbeToolAsync("cmake", ["--version"], "CMake", ProbeIsolationRoot, ct).ConfigureAwait(false),
            await ProbeToolAsync("gcc", ["--version"], "C compiler (gcc)", ProbeIsolationRoot, ct).ConfigureAwait(false),
            await ProbeToolAsync("g++", ["--version"], "C++ compiler (g++)", ProbeIsolationRoot, ct).ConfigureAwait(false),
            await ProbeEitherToolAsync(ct).ConfigureAwait(false),
            await ProbeToolAsync("git", ["--version"], "git", ProbeIsolationRoot, ct).ConfigureAwait(false),

            // The post-build relocation gate runs readelf. Without binutils a build compiles for up to two hours and
            // then fails on a check the operator could have satisfied in seconds, so it is a checklist row like the
            // compilers, and it is required for every backend because the gate is backend-independent.
            await ProbeToolAsync("readelf", ["--version"], "readelf (binutils)", ProbeIsolationRoot, ct).ConfigureAwait(false),
            ProbeFreeDisk()
        };

        // CUDA is the only accelerated backend whisper has here, so there is no Vulkan branch to mirror the image
        // runtime's. A CPU build needs nothing beyond the rows above.
        if (backend == WhisperBackend.Cuda)
        {
            items.Insert(1, await ProbeToolAsync("nvcc", ["--version"], "NVIDIA CUDA compiler (nvcc)", ProbeIsolationRoot, ct).ConfigureAwait(false));
            items.Insert(2,
                await ProbeToolAsync("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], "NVIDIA driver probe", ProbeIsolationRoot, ct)
                    .ConfigureAwait(false));
        }

        return new WhisperCppSourceBuildPrerequisiteReport(items.TrueForAll(static item => item.Satisfied), items);
    }

    internal static Task<WhisperCppSourceBuildPrerequisiteItem> ProbeToolForTestsAsync(string fileName,
        IReadOnlyList<string> arguments,
        string displayName,
        CancellationToken ct,
        string? isolationRoot = null)
    {
        isolationRoot ??= Path.Combine(Path.GetTempPath(),
            "xe-local-ai-engine",
            "whisper-source-probe-tests",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return ProbeToolAsync(fileName, arguments, displayName, isolationRoot, ct);
    }

    // Either build driver satisfies the same requirement, so they share one row: reporting "ninja missing" on a box
    // that builds fine with make would be a false blocker.
    private async Task<WhisperCppSourceBuildPrerequisiteItem> ProbeEitherToolAsync(CancellationToken ct)
    {
        var ninja = await ProbeToolAsync("ninja", ["--version"], "Ninja", ProbeIsolationRoot, ct).ConfigureAwait(false);
        if (ninja.Satisfied)
        {
            return ninja with
            {
                Key = "make-or-ninja"
            };
        }

        var make = await ProbeToolAsync("make", ["--version"], "Make", ProbeIsolationRoot, ct).ConfigureAwait(false);
        return make with
        {
            Key = "make-or-ninja",
            Detail = make.Satisfied ? make.Detail : "Neither Ninja nor Make is available."
        };
    }

    private WhisperCppSourceBuildPrerequisiteItem ProbeFreeDisk()
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var root = Path.GetPathRoot(Path.GetFullPath(_cacheRoot));
            var available = string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
            return new WhisperCppSourceBuildPrerequisiteItem("free-disk",
                available >= _requiredFreeDiskBytes,
                available >= _requiredFreeDiskBytes
                    ? "Sufficient free disk space detected."
                    : "At least 15 GiB of free disk space is required.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new WhisperCppSourceBuildPrerequisiteItem("free-disk", false, "Free disk space could not be verified.");
        }
    }

    private static async Task<WhisperCppSourceBuildPrerequisiteItem> ProbeToolAsync(string fileName,
        IReadOnlyList<string> arguments,
        string displayName,
        string isolationRoot,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
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

            WhisperSourceProcessHardening.Configure(process.StartInfo, isolationRoot);
            if (!process.Start())
            {
                return Missing(fileName, displayName);
            }

            WhisperSourceProcessHardening.CloseStandardInput(process);
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
                    ? new WhisperCppSourceBuildPrerequisiteItem(fileName, true, firstLine ?? $"{displayName} detected.")
                    : Missing(fileName, displayName);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                await IgnoreCancellationAsync(outputTask).ConfigureAwait(false);
                await IgnoreCancellationAsync(errorTask).ConfigureAwait(false);
                return new WhisperCppSourceBuildPrerequisiteItem(fileName, false, $"{displayName} probe timed out.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryKill(process);
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

    private static WhisperCppSourceBuildPrerequisiteItem Missing(string key, string displayName)
    {
        return new WhisperCppSourceBuildPrerequisiteItem(key, false, $"{displayName} is not available.");
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(milliseconds: 5000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Best-effort bounded probe cleanup.
        }
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine");
    }
}
