namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Bounded Linux source-build prerequisite checks.</summary>
public sealed class StableDiffusionCppSourceBuildPrerequisiteProbe : IStableDiffusionCppSourceBuildPrerequisiteProbe
{
    private const int MaxProbeOutputChars = 4096;
    internal const long RequiredFreeDiskBytes = 15L * 1024 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private readonly string _cacheRoot;
    private readonly long _requiredFreeDiskBytes;

    public StableDiffusionCppSourceBuildPrerequisiteProbe()
        : this(DefaultCacheRoot(), RequiredFreeDiskBytes)
    {
    }

    internal StableDiffusionCppSourceBuildPrerequisiteProbe(string cacheRoot, long requiredFreeDiskBytes = RequiredFreeDiskBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _requiredFreeDiskBytes = requiredFreeDiskBytes;
    }

    public async Task<StableDiffusionCppSourceBuildPrerequisiteReport> ProbeAsync(SdGpuBackend backend, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new StableDiffusionCppSourceBuildPrerequisiteReport(false,
            [
                new StableDiffusionCppSourceBuildPrerequisiteItem("os-is-linux", false, "In-app source builds are available on Linux only.")
            ]);
        }

        var items = new List<StableDiffusionCppSourceBuildPrerequisiteItem>
        {
            new("os-is-linux", true, "Linux host detected."),
            await ProbeToolAsync("cmake", ["--version"], "CMake", ProbeIsolationRoot, ct).ConfigureAwait(false),
            await ProbeToolAsync("gcc", ["--version"], "C compiler (gcc)", ProbeIsolationRoot, ct).ConfigureAwait(false),
            await ProbeToolAsync("g++", ["--version"], "C++ compiler (g++)", ProbeIsolationRoot, ct).ConfigureAwait(false),
            await ProbeEitherToolAsync(ct).ConfigureAwait(false),
            await ProbeToolAsync("git", ["--version"], "git", ProbeIsolationRoot, ct).ConfigureAwait(false),
            ProbeFreeDisk()
        };

        if (backend == SdGpuBackend.Cuda)
        {
            items.Insert(1, await ProbeToolAsync("nvcc", ["--version"], "NVIDIA CUDA compiler (nvcc)", ProbeIsolationRoot, ct).ConfigureAwait(false));
            items.Insert(2, await ProbeToolAsync("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], "NVIDIA driver probe", ProbeIsolationRoot, ct).ConfigureAwait(false));
        }
        else if (backend == SdGpuBackend.Vulkan)
        {
            items.Insert(1, await ProbeToolAsync("glslc", ["--version"], "Vulkan shader compiler (glslc)", ProbeIsolationRoot, ct).ConfigureAwait(false));
            items.Insert(2, await ProbeToolAsync("vulkaninfo", ["--summary"], "Vulkan runtime probe", ProbeIsolationRoot, ct).ConfigureAwait(false));
        }

        return new StableDiffusionCppSourceBuildPrerequisiteReport(items.TrueForAll(static item => item.Satisfied), items);
    }

    private async Task<StableDiffusionCppSourceBuildPrerequisiteItem> ProbeEitherToolAsync(CancellationToken ct)
    {
        var ninja = await ProbeToolAsync("ninja", ["--version"], "Ninja", ProbeIsolationRoot, ct).ConfigureAwait(false);
        if (ninja.Satisfied)
        {
            return ninja with { Key = "make-or-ninja" };
        }

        var make = await ProbeToolAsync("make", ["--version"], "Make", ProbeIsolationRoot, ct).ConfigureAwait(false);
        return make with
        {
            Key = "make-or-ninja",
            Detail = make.Satisfied ? make.Detail : "Neither Ninja nor Make is available."
        };
    }

    private StableDiffusionCppSourceBuildPrerequisiteItem ProbeFreeDisk()
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var root = Path.GetPathRoot(Path.GetFullPath(_cacheRoot));
            var available = string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
            return new StableDiffusionCppSourceBuildPrerequisiteItem("free-disk",
                available >= _requiredFreeDiskBytes,
                available >= _requiredFreeDiskBytes ? "Sufficient free disk space detected." : "At least 15 GiB of free disk space is required.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new StableDiffusionCppSourceBuildPrerequisiteItem("free-disk", false, "Free disk space could not be verified.");
        }
    }

    private static async Task<StableDiffusionCppSourceBuildPrerequisiteItem> ProbeToolAsync(
        string fileName,
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

            StableDiffusionSourceProcessHardening.Configure(process.StartInfo, isolationRoot);
            if (!process.Start())
            {
                return Missing(fileName, displayName);
            }

            StableDiffusionSourceProcessHardening.CloseStandardInput(process);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);
                var firstLine = (output + Environment.NewLine + error)
                                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .FirstOrDefault();
                return process.ExitCode == 0
                    ? new StableDiffusionCppSourceBuildPrerequisiteItem(fileName, true, firstLine ?? $"{displayName} detected.")
                    : Missing(fileName, displayName);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                await IgnoreCancellationAsync(outputTask).ConfigureAwait(false);
                await IgnoreCancellationAsync(errorTask).ConfigureAwait(false);
                return new StableDiffusionCppSourceBuildPrerequisiteItem(fileName, false, $"{displayName} probe timed out.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryKill(process);
                await IgnoreCancellationAsync(outputTask).ConfigureAwait(false);
                await IgnoreCancellationAsync(errorTask).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Missing(fileName, displayName);
        }
    }

    internal static Task<StableDiffusionCppSourceBuildPrerequisiteItem> ProbeToolForTestsAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string displayName,
        CancellationToken ct,
        string? isolationRoot = null)
    {
        isolationRoot ??= Path.Combine(
            Path.GetTempPath(),
            "xe-local-ai-engine",
            "stable-diffusion-source-probe-tests",
            Environment.ProcessId.ToString());
        return ProbeToolAsync(fileName, arguments, displayName, isolationRoot, ct);
    }

    private string ProbeIsolationRoot => Path.Combine(_cacheRoot, "stable-diffusion.cpp", "source-build", ".probe");

    private static StableDiffusionCppSourceBuildPrerequisiteItem Missing(string key, string displayName)
    {
        return new StableDiffusionCppSourceBuildPrerequisiteItem(key, false, $"{displayName} is not available.");
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The probe process was killed after timeout or caller cancellation.
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var output = new System.Text.StringBuilder(MaxProbeOutputChars);
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best-effort bounded probe cleanup.
        }
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine");
    }
}
