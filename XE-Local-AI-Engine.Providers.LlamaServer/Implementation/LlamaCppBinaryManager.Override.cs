namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;

/// <summary>
///     Operator bring-your-own llama-server override branch of <see cref="LlamaCppBinaryManager" />. When the override is
///     active <see cref="EnsureBinaryAsync" /> delegates here instead of running the download → hash-verify → cache
///     pipeline: the supplied binary is validated and served as-is. The SHA256 pin is intentionally absent (an operator
///     file has no publisher digest), so this branch enforces the compensating controls — regular-file, ownership +
///     non-world-writable (TOCTOU-swap hardening), exec bit, a <c>--version</c> smoke test, and, for a GPU variant, a
///     <c>--list-devices</c> GPU-presence check so a CPU/Vulkan binary mis-tagged as CUDA cannot silently run wrong.
/// </summary>
public sealed partial class LlamaCppBinaryManager
{
    // Bounds the override --list-devices backend check; mirrors the VRAM probe / smoke-test timeout. On overrun the child
    // is tree-killed and the check fails (no silent pass).
    private static readonly TimeSpan OverrideDeviceCheckTimeout = TimeSpan.FromSeconds(15);

    // Unix st_mode bit masks (POSIX). Used to classify the override target and its parent dir.
    private const uint StatFileTypeMask = 0xF000; // S_IFMT
    private const uint StatRegularFile = 0x8000; // S_IFREG
    private const uint StatDirectory = 0x4000; // S_IFDIR
    private const uint StatRootUid = 0; // root is a trusted owner alongside the running euid

    /// <summary>
    ///     Validates and returns the operator-supplied <c>llama-server</c> without any download/cache/state mutation. Any
    ///     validation failure throws a sanitized <see cref="LlamaRuntimeException" /> — the override NEVER falls through to
    ///     acquisition or degrades to a silent CPU run. The returned <see cref="LlamaBinary.Variant" /> is the OVERRIDE's
    ///     configured variant, not the caller-passed variant, and <see cref="LlamaBinary.Version" /> is the sentinel
    ///     <c>"override"</c> (surfaced verbatim in the runtime version card).
    /// </summary>
    private static async Task<LlamaBinary> ResolveOverrideBinaryAsync(LlamaServerRuntimeOverrideOptions options, CancellationToken ct)
    {
        // IsActive guarantees a non-blank path; the local is non-null.
        var serverPath = options.ServerPath!;

        // Require an absolute path — there is no base dir to resolve a relative one against, and a relative override is
        // almost certainly a misconfiguration. (The `..` "traversal" framing does not apply: there is no contained base.)
        if (!Path.IsPathFullyQualified(serverPath))
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override path must be an absolute path.");
        }

        var fullPath = Path.GetFullPath(serverPath);

        // File.Exists is true only for an existing non-directory path; it follows symlinks, so a symlink-to-directory
        // resolves to false here. A non-regular special file (device/FIFO) is rejected by the stat type check below.
        if (!File.Exists(fullPath))
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override path does not point to an existing file.");
        }

        ValidateOverrideFileSecurity(fullPath);

        // Self-check: the binary must at least report its version (spawn succeeds, runtime libs resolve). Reuses the same
        // smoke test the acquisition path uses; the child inherits the parent env (operator-exported LD_LIBRARY_PATH).
        if (!await SmokeTestAsync(fullPath, ct).ConfigureAwait(false))
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override failed its self-check.");
        }

        // No-silent-CPU invariant: a non-CPU variant MUST enumerate at least one GPU device, or a CPU/Vulkan binary
        // mis-tagged as CUDA would otherwise run wrong-but-green. CPU variant skips this check (it has no GPU device list).
        if (options.Variant != GpuVariant.Cpu && !await OverrideExposesGpuDeviceAsync(fullPath, ct).ConfigureAwait(false))
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override exposes no GPU device for the requested acceleration variant.");
        }

        return new LlamaBinary(fullPath, "override", options.Variant, IsPinnedFallback: false);
    }

    /// <summary>
    ///     Unix-only hardening for the override binary and its parent directory: both must be operator-owned (the running
    ///     euid, or root) and not world-writable, the binary must be a regular file with an exec bit, and the parent must
    ///     be a directory. These compensate for the absent SHA256 swap-detection on a multi-user host. No-op on Windows,
    ///     where the regular-file existence check plus the smoke test are the available controls (the override's target
    ///     platform is Linux).
    /// </summary>
    private static void ValidateOverrideFileSecurity(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        EnsureUnixPathSecure(fullPath, requireExecutable: true, requireRegularFile: true);

        var parentDir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parentDir))
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override path has no resolvable parent directory.");
        }

        EnsureUnixPathSecure(parentDir, requireExecutable: false, requireRegularFile: false);
    }

    /// <summary>
    ///     Enforces the per-path Unix controls: not world-writable (managed <see cref="File.GetUnixFileMode(string)" />),
    ///     an exec bit when required, and — when the platform stat seam is available — operator ownership and the expected
    ///     file type (regular file vs directory). When stat is unavailable (an unsupported arch/libc), the managed
    ///     permission checks still apply; the smoke test would reject a non-executable or special-file target regardless.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private static void EnsureUnixPathSecure(string path, bool requireExecutable, bool requireRegularFile)
    {
        var mode = File.GetUnixFileMode(path);

        if ((mode & UnixFileMode.OtherWrite) != UnixFileMode.None)
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override (or its directory) is world-writable; tighten its permissions before use.");
        }

        if (requireExecutable && (mode & UnixFileMode.UserExecute) == UnixFileMode.None)
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override is not marked executable.");
        }

        if (!TryStatUnix(path, out var statMode, out var ownerUid))
        {
            // The ownership/type stat seam is unavailable on this platform; the managed permission checks above stand.
            return;
        }

        var fileType = statMode & StatFileTypeMask;
        if (requireRegularFile && fileType != StatRegularFile)
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override path is not a regular file.");
        }

        if (!requireRegularFile && fileType != StatDirectory)
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override path's parent is not a directory.");
        }

        // Operator-owned = owned by the running euid, or root (a root-owned binary cannot be swapped by the app user).
        var euid = GetEuid();
        if (ownerUid != euid && ownerUid != StatRootUid)
        {
            throw new LlamaRuntimeException("The operator-supplied llama-server override (or its directory) is not owned by the operator running the application.");
        }
    }

    /// <summary>
    ///     Spawns <c>&lt;override&gt; --list-devices</c> (bounded, tree-killed) and reports whether at least one GPU device
    ///     is enumerated. Mirrors the VRAM probe's spawn/drain/timeout pattern and reuses its proven device-line shape
    ///     (the "<c>(&lt;total&gt; MiB, &lt;free&gt; MiB free)</c>" column appears only for GPU devices). A launch failure,
    ///     timeout, or device-less output reports <see langword="false" /> so the caller rejects the override.
    /// </summary>
    private static async Task<bool> OverrideExposesGpuDeviceAsync(string serverPath, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = serverPath,

                // Co-locate the working dir with the binary so its bundled/host runtime libraries resolve (mirrors the
                // supervised launcher and the VRAM probe). Args are passed separately — no shell, no injection surface.
                WorkingDirectory = Path.GetDirectoryName(serverPath) ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--list-devices");

        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Win32Exception)
        {
            // The binary became unspawnable between the smoke test and here (TOCTOU / vanished file) → fail closed so
            // the caller rejects the override rather than the raw OS error escaping unsanitized.
            return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(OverrideDeviceCheckTimeout);
        try
        {
            // Drain BOTH pipes concurrently: llama.cpp prints the device table to stdout and its backend banner to
            // stderr, and an undrained redirected pipe can stall the child. Combine both before scanning for a device.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return GpuDeviceRegex().IsMatch(string.Concat(stdout, "\n", stderr));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The probe overran the bound (the caller's own token did NOT fire) → treat as "no GPU device confirmed".
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological device output blew the regex budget → treat as "no GPU device confirmed" (fail closed).
            return false;
        }
        finally
        {
            TryKill(process);
        }
    }

    /// <summary>
    ///     Reads the override target's Unix <c>st_mode</c> and <c>st_uid</c> via libc <c>stat</c>. Returns
    ///     <see langword="false" /> when the seam is unavailable (an unsupported architecture, or <c>stat</c> not exported)
    ///     so the caller degrades to the managed permission checks rather than failing a valid binary.
    /// </summary>
    private static bool TryStatUnix(string path, out uint mode, out uint ownerUid)
    {
        mode = 0;
        ownerUid = 0;

        // glibc struct stat field offsets differ by architecture. Only the common 64-bit layouts are mapped; an unmapped
        // arch falls back to the managed checks.
        var (modeOffset, uidOffset) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (24, 28),
            Architecture.Arm64 => (16, 24),
            _ => (-1, -1)
        };

        if (modeOffset < 0)
        {
            return false;
        }

        var buffer = new byte[256];
        try
        {
            if (StatNative(path, buffer) != 0)
            {
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }

        mode = BitConverter.ToUInt32(buffer, modeOffset);
        ownerUid = BitConverter.ToUInt32(buffer, uidOffset);
        return true;
    }

    // A GPU device line carries the "(<total> MiB, <free> MiB free)" capacity column; CPU/no-GPU output has none. Reuses
    // the VRAM probe's device-column shape so the GPU-presence signal stays consistent across the codebase.
    [GeneratedRegex(@"[0-9]+\s*MiB\s*,\s*[0-9]+\s*MiB\s*free",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex GpuDeviceRegex();

    // int stat(const char* path, struct stat* buf); — 0 on success. The buffer is sized for the largest 64-bit layout.
    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int StatNative(string path, byte[] buffer);

    // uid_t geteuid(void); — the effective user id of the running process.
    [LibraryImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial uint GetEuid();
}
