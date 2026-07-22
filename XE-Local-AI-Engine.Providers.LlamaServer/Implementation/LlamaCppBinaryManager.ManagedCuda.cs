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
    private async Task<LlamaBinary?> TryServeManagedSourceBinaryAsync(InstalledRuntimeState installed, CancellationToken ct)
    {
        var buildBinDir = installed.SourceBuildPath!;
        var serverPath = Path.Combine(buildBinDir, ManagedCudaServerFileName);

        try
        {
            if (!File.Exists(serverPath) || new FileInfo(serverPath).LinkTarget is not null)
            {
                await DiscardManagedCudaRecordAsync(ct).ConfigureAwait(false);
                return null;
            }

            // Full path-chain perms/ownership (every ancestor under cacheRoot) — throws on a world-writable/untrusted hop.
            ValidateManagedTreeLinks(buildBinDir);
            EnsureManagedPathChainSecure(serverPath);

            // Recorded-SHA256 recompare: a swapped binary (same path, different bytes) is rejected.
            if (!await HashMatchesAsync(serverPath, installed.Sha256, ct).ConfigureAwait(false))
            {
                await DiscardManagedCudaRecordAsync(ct).ConfigureAwait(false);
                return null;
            }

            return new LlamaBinary(serverPath, installed.Tag, installed.Variant, IsPinnedFallback: false);
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
        return await AdoptSourceBuildAsync(buildBinDir,
            tag,
            GpuVariant.Cuda,
            LlamaCppSourceBuildRequestValidation.OfficialRepository,
            LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned,
            requestedCommit: null,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<InstalledRuntimeState> AdoptSourceBuildAsync(string buildBinDir,
        string tag,
        GpuVariant variant,
        string sourceRepository,
        string sourceCommit,
        LlamaCppSourceRevisionMode revisionMode,
        string? requestedCommit,
        CancellationToken ct)
    {
        await _sourceMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildBinDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        if (!IsValidTag(tag))
        {
            throw new LlamaRuntimeException("The source build tag is not in a recognized format.");
        }

        var fullBinDir = Path.GetFullPath(buildBinDir);
        var serverPath = Path.Combine(fullBinDir, ManagedCudaServerFileName);
        if (!File.Exists(serverPath) || new FileInfo(serverPath).LinkTarget is not null)
        {
            throw new LlamaRuntimeException("The source build did not produce the expected server executable.");
        }

        // Adoption validation: full path-chain perms/ownership + --version smoke + --list-devices GPU presence. A binary
        // that cannot self-check, or that exposes no GPU device, is never adopted (it would otherwise silently run CPU).
        ValidateManagedTreeLinks(fullBinDir);
        EnsureManagedPathChainSecure(serverPath);

        if (!await SmokeTestAsync(serverPath, ct).ConfigureAwait(false))
        {
            throw new LlamaRuntimeException("The source-built llama-server failed its post-build self-check.");
        }

        if (variant != GpuVariant.Cpu && !await SourceBuildExposesDeviceAsync(serverPath, variant, ct).ConfigureAwait(false))
        {
            throw new LlamaRuntimeException($"The source-built llama-server exposes no {variant} device; the requested backend was not built correctly.");
        }

        var sha256 = await ComputeFileSha256Async(serverPath, ct).ConfigureAwait(false);

        var state = new InstalledRuntimeState(tag,
            Asset: variant switch
            {
                GpuVariant.Cpu => "(source-build:cpu)",
                GpuVariant.Vulkan => "(source-build:vulkan)",
                GpuVariant.Cuda => ManagedCudaSourceBuildSentinel,
                _ => "(source-build)"
            },
            Sha256: sha256,
            Variant: variant,
            InstalledAtUtc: DateTimeOffset.UtcNow,
            SourceBuildPath: fullBinDir,
            SourceRepository: sourceRepository,
            SourceCommit: Convert.ToHexStringLower(Convert.FromHexString(sourceCommit)),
            SourceRevisionMode: revisionMode,
            SourceRequestedCommit: requestedCommit is null ? null : Convert.ToHexStringLower(Convert.FromHexString(requestedCommit)));

        if (_installedRuntimeStore is not null)
        {
            await _installedRuntimeStore.WriteAsync(state, ct).ConfigureAwait(false);
        }

        // Cached signal: the variant selector now returns Cuda on a Linux NVIDIA box without a per-call store read.
        _managedCudaSignal?.SetActive(variant);

        return state;
        }
        finally
        {
            _sourceMutationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveCudaSourceBuildAsync(CancellationToken ct)
    {
        await RemoveSourceBuildCoreAsync(legacyOnly: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveSourceBuildAsync(CancellationToken ct)
    {
        await RemoveSourceBuildCoreAsync(legacyOnly: false, ct).ConfigureAwait(false);
    }

    private async Task RemoveSourceBuildCoreAsync(bool legacyOnly, CancellationToken ct)
    {
        await _sourceMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var installed = _installedRuntimeStore is null
                ? null
                : await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
            if (installed?.SourceBuildPath is not { Length: > 0 } sourceBuildPath
                || legacyOnly && !installed.IsLegacyPinnedCuda())
            {
                return;
            }

            var fullRecordedPath = Path.GetFullPath(sourceBuildPath);
            var activeTree = Path.GetFullPath(Path.Combine(_cacheRoot, "llama.cpp", "source-build", "active"));
            var activeBin = Path.Combine(activeTree, "build", "bin");
            var legacyTree = Path.GetFullPath(Path.Combine(_cacheRoot, "llama.cpp", "source-cuda", LlamaCppReleasePins.PinnedTag));
            var legacyBin = Path.Combine(legacyTree, "build", "bin");
            string? treeToDelete = null;
            if (string.Equals(fullRecordedPath, activeBin, StringComparison.Ordinal))
            {
                treeToDelete = activeTree;
            }
            else if (legacyOnly && string.Equals(fullRecordedPath, legacyBin, StringComparison.Ordinal))
            {
                treeToDelete = legacyTree;
            }

            if (treeToDelete is null)
            {
                return;
            }

            if (Directory.Exists(treeToDelete))
            {
                Directory.Delete(treeToDelete, recursive: true);
            }

            _managedCudaSignal?.Clear();
            if (_installedRuntimeStore is not null)
            {
                await _installedRuntimeStore.DeleteAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sourceMutationGate.Release();
        }
    }

    /// <summary>The sentinel <see cref="InstalledRuntimeState.Asset" /> for a managed CUDA source build; readers must key off <see cref="InstalledRuntimeState.SourceBuildPath" />, never parse this.</summary>
    internal const string ManagedCudaSourceBuildSentinel = "(source-build:cuda)";

    // Discards an invalid/missing managed CUDA record (delete the runtime record + clear the cached signal). Best-effort
    // on the store delete: a delete failure must not mask the fall-through.
    private async Task DiscardManagedCudaRecordAsync(CancellationToken ct)
    {
        await _sourceMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _managedCudaSignal?.Clear();
            if (_installedRuntimeStore is not null)
            {
                await _installedRuntimeStore.DeleteAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sourceMutationGate.Release();
        }
    }

    private static async Task<bool> SourceBuildExposesDeviceAsync(string serverPath, GpuVariant variant, CancellationToken ct)
    {
        var expectedPrefix = variant == GpuVariant.Cuda ? "CUDA" : "Vulkan";
        var startInfo = new System.Diagnostics.ProcessStartInfo(serverPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--list-devices");
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
            return process.ExitCode == 0 && output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                                                  .Any(line => DeviceLineRegex().IsMatch(line)
                                                               && line.TrimStart().StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
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
                // Best-effort process cleanup.
            }
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
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                throw new LlamaRuntimeException("The source-built llama-server path contains a linked directory.");
            }
            EnsureUnixPathSecure(directory, requireExecutable: false, requireRegularFile: false);
            if (string.Equals(directory, root, StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private static void ValidateManagedTreeLinks(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            var directoryInfo = new DirectoryInfo(directory);
            if (directoryInfo.LinkTarget is not null)
            {
                throw new LlamaRuntimeException("The managed source runtime contains a linked directory.");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                var file = new FileInfo(entry);
                if (file.LinkTarget is null)
                {
                    continue;
                }

                var visited = new HashSet<string>(StringComparer.Ordinal);
                var current = entry;
                for (var hop = 0; hop < 32; hop++)
                {
                    if (!visited.Add(current))
                    {
                        throw new LlamaRuntimeException("The managed source runtime contains a cyclic library link.");
                    }

                    var currentInfo = new FileInfo(current);
                    var target = currentInfo.LinkTarget;
                    if (target is null)
                    {
                        if (!currentInfo.Exists)
                        {
                            throw new LlamaRuntimeException("The managed source runtime contains a dangling library link.");
                        }

                        break;
                    }

                    if (Path.IsPathRooted(target))
                    {
                        throw new LlamaRuntimeException("The managed source runtime contains an absolute library link.");
                    }

                    current = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, target));
                    if (!current.StartsWith(rootPrefix, StringComparison.Ordinal) || Directory.Exists(current))
                    {
                        throw new LlamaRuntimeException("The managed source runtime contains an escaping or directory library link.");
                    }

                    if (hop == 31)
                    {
                        throw new LlamaRuntimeException("The managed source runtime contains an excessively deep library link chain.");
                    }
                }
            }
        }
    }

    /// <summary>Computes the lowercase-hex SHA256 of a file (the local digest recorded for a source build).</summary>
    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?:CUDA|Vulkan)[0-9]+:",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex DeviceLineRegex();
}
