namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Managed source-built CUDA runtime branch of <see cref="LlamaCppBinaryManager" />. Upstream ships no prebuilt Linux
///     CUDA asset, so an in-app build (<see cref="Contracts.ICudaBuildService" />) produces a <c>llama-server</c> the
///     engine adopts as a managed runtime. This partial owns the runtime-record side of that: adoption validation +
///     recording (<see cref="AdoptCudaSourceBuildAsync" />) and the every-serve re-validation
///     (<see cref="TryServeManagedCudaBinaryAsync" />).
/// </summary>
/// <remarks>
///     <para>
///         The built binary carries no publisher SHA256 (it was produced locally), so adoption records the SHA256 of the
///         on-disk binary and every serve recomputes + recompares it, alongside a FULL path-chain perms/ownership walk
///         (every ancestor from the cache root down is non-world-writable + owner-trusted). Together these close the
///         deep-tree binary-swap and the adopt→restart→serve TOCTOU windows. <c>[secHIGH-3]</c> <c>[secMED-2]</c>
///     </para>
///     <para>
///         No-silent-CPU invariant: a recorded build that is missing or fails validation at serve time clears the record +
///         the cached signal and falls through to the normal acquisition path — which, for a Cuda request on Linux (no
///         prebuilt), throws the sanitized "no prebuilt for this OS/arch" rather than serving CPU as if it were CUDA.
///     </para>
/// </remarks>
public sealed partial class LlamaCppBinaryManager
{
    /// <summary>The built-server file name inside a managed source-build bin directory.</summary>
    private const string ManagedCudaServerFileName = "llama-server";

    /// <summary>
    ///     Serve-time resolution of a recorded managed CUDA build. Re-validates the full path chain + recorded SHA256 (no
    ///     smoke/device check on the serve hot path — those run once at adoption). Returns the validated binary, or
    ///     <see langword="null" /> when the recorded build is missing/invalid — in which case the record + cached signal are
    ///     cleared so the caller falls through to the normal path (graceful self-heal; never a silent CPU serve).
    /// </summary>
    private async Task<LlamaBinary?> TryServeManagedCudaBinaryAsync(InstalledRuntimeState installed, CancellationToken ct)
    {
        var buildBinDir = installed.SourceBuildPath!;
        var serverPath = Path.Combine(buildBinDir, ManagedCudaServerFileName);

        try
        {
            if (!File.Exists(serverPath))
            {
                await DiscardManagedCudaRecordAsync(ct).ConfigureAwait(false);
                return null;
            }

            // Full path-chain perms/ownership (every ancestor under cacheRoot) — throws on a world-writable/untrusted hop.
            EnsureManagedPathChainSecure(serverPath);

            // Recorded-SHA256 recompare: a swapped binary (same path, different bytes) is rejected.
            if (!await HashMatchesAsync(serverPath, installed.Sha256, ct).ConfigureAwait(false))
            {
                await DiscardManagedCudaRecordAsync(ct).ConfigureAwait(false);
                return null;
            }

            return new LlamaBinary(serverPath, installed.Tag, GpuVariant.Cuda, IsPinnedFallback: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LlamaRuntimeException)
        {
            // A path-chain perms/ownership violation invalidates the recorded build — discard it + the signal and fall
            // through to the normal path (which throws the sanitized no-prebuilt failure for a Cuda request on Linux).
            await DiscardManagedCudaRecordAsync(ct).ConfigureAwait(false);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildBinDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        if (!IsValidTag(tag))
        {
            throw new LlamaRuntimeException("The source build tag is not in a recognized format.");
        }

        var fullBinDir = Path.GetFullPath(buildBinDir);
        var serverPath = Path.Combine(fullBinDir, ManagedCudaServerFileName);
        if (!File.Exists(serverPath))
        {
            throw new LlamaRuntimeException("The source build did not produce the expected server executable.");
        }

        // Adoption validation: full path-chain perms/ownership + --version smoke + --list-devices GPU presence. A binary
        // that cannot self-check, or that exposes no GPU device, is never adopted (it would otherwise silently run CPU).
        EnsureManagedPathChainSecure(serverPath);

        if (!await SmokeTestAsync(serverPath, ct).ConfigureAwait(false))
        {
            throw new LlamaRuntimeException("The source-built llama-server failed its post-build self-check.");
        }

        if (!await OverrideExposesGpuDeviceAsync(serverPath, ct).ConfigureAwait(false))
        {
            throw new LlamaRuntimeException("The source-built llama-server exposes no GPU device; the build did not produce a working CUDA runtime.");
        }

        var sha256 = await ComputeFileSha256Async(serverPath, ct).ConfigureAwait(false);

        var state = new InstalledRuntimeState(tag,
            Asset: ManagedCudaSourceBuildSentinel,
            Sha256: sha256,
            Variant: GpuVariant.Cuda,
            InstalledAtUtc: DateTimeOffset.UtcNow,
            SourceBuildPath: fullBinDir);

        if (_installedRuntimeStore is not null)
        {
            await _installedRuntimeStore.WriteAsync(state, ct).ConfigureAwait(false);
        }

        // Cached signal: the variant selector now returns Cuda on a Linux NVIDIA box without a per-call store read.
        _managedCudaSignal?.MarkAvailable();

        return state;
    }

    /// <inheritdoc />
    public async Task RemoveCudaSourceBuildAsync(CancellationToken ct)
    {
        var installed = _installedRuntimeStore is null
            ? null
            : await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);

        if (installed?.SourceBuildPath is { Length: > 0 } sourceBuildPath)
        {
            // Path-guard: delete ONLY within {cacheRoot}/llama.cpp/source-cuda/. Assert the recorded path is a normalized
            // child of that root before removing the whole source-cuda tree. [secMED-3]
            var sourceCudaRoot = Path.GetFullPath(Path.Combine(_cacheRoot, "llama.cpp", "source-cuda"));
            var rootWithSeparator = sourceCudaRoot.EndsWith(Path.DirectorySeparatorChar)
                ? sourceCudaRoot
                : sourceCudaRoot + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(sourceBuildPath).StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                TryDeleteDirectory(sourceCudaRoot);
            }
        }

        await DiscardManagedCudaRecordAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The sentinel <see cref="InstalledRuntimeState.Asset" /> for a managed CUDA source build; readers must key off <see cref="InstalledRuntimeState.SourceBuildPath" />, never parse this.</summary>
    internal const string ManagedCudaSourceBuildSentinel = "(source-build:cuda)";

    // Discards an invalid/missing managed CUDA record (delete the runtime record + clear the cached signal). Best-effort
    // on the store delete: a delete failure must not mask the fall-through.
    private async Task DiscardManagedCudaRecordAsync(CancellationToken ct)
    {
        _managedCudaSignal?.Clear();
        if (_installedRuntimeStore is not null)
        {
            await _installedRuntimeStore.DeleteAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Generalized full path-chain security walk (override's immediate-parent check extended to the whole chain): the
    ///     binary must be a regular, executable, non-world-writable, owner-trusted file, and EVERY ancestor directory from
    ///     the cache root down must be a non-world-writable, owner-trusted directory. The binary must also be a normalized
    ///     child of the cache root. No-op on Windows (the managed build is Linux-only). <c>[secHIGH-3]</c>
    /// </summary>
    private void EnsureManagedPathChainSecure(string serverPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetFullPath(_cacheRoot);
        var full = Path.GetFullPath(serverPath);

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new LlamaRuntimeException("The source-built llama-server is outside the managed runtime cache directory.");
        }

        // The binary itself: regular file + exec bit + not world-writable + owner-trusted.
        EnsureUnixPathSecure(full, requireExecutable: true, requireRegularFile: true);

        // Every ancestor directory from the binary's parent up to AND INCLUDING the cache root.
        var directory = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(directory) && directory.Length >= root.Length)
        {
            EnsureUnixPathSecure(directory, requireExecutable: false, requireRegularFile: false);
            if (string.Equals(directory, root, StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    /// <summary>Computes the lowercase-hex SHA256 of a file (the local digest recorded for a source build).</summary>
    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
