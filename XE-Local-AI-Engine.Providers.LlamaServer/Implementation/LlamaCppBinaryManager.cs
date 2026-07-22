namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaCppBinaryManager" />: resolves the pinned prebuilt asset for the host, downloads it
///     over HTTP, verifies its SHA256 against <see cref="LlamaCppReleasePins" />, extracts it under a stable cache
///     directory, and returns the resolved <c>llama-server</c> path. Never source-builds.
/// </summary>
/// <remarks>
///     <para>
///         Cache layout: <c>{cacheRoot}/llama.cpp/{tag}/{variant}/</c> holds the extracted archive; a hash-valid
///         cached binary is reused without re-download (offline path). A user-selected upgrade is cached under its own
///         <c>{tag}</c> directory, so the recommended-pinned fallback is never deleted by an upgrade.
///     </para>
///     <para>
///         On SHA256 mismatch the partial download is discarded and retried <em>once</em>; a second mismatch surfaces
///         a sanitized <see cref="LlamaRuntimeException" /> (no internal paths/URLs in the message).
///     </para>
/// </remarks>
public sealed partial class LlamaCppBinaryManager : ILlamaCppBinaryManager
{
    private static readonly TimeSpan SmokeTestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Absolute hard ceiling on a single runtime download. A prebuilt llama.cpp asset is well under this; the cap is a
    ///     disk-exhaustion guard against a hostile/buggy server streaming an unbounded body. Enforced on every download
    ///     path; the size-aware <see cref="InstallTagAsync" /> path tightens it further with the catalog-reported size.
    /// </summary>
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Slack added to the catalog-reported size before aborting an oversized stream (still capped at the ceiling).</summary>
    private const long DownloadSizeSlackBytes = 1L * 1024 * 1024;

    private readonly string _activeTag;
    private readonly Architecture _arch;
    private readonly string _cacheRoot;
    private readonly ILlamaCppReleaseCatalog? _catalog;
    private readonly HttpClient _httpClient;
    private readonly IInstalledRuntimeStore? _installedRuntimeStore;
    private readonly ICudaManagedBuildSignal? _managedCudaSignal;
    private readonly OSPlatform _os;
    private readonly LlamaServerRuntimeOverrideOptions? _overrideOptions;

    /// <summary>
    ///     Creates a binary manager that downloads through <paramref name="httpClient" /> and caches under
    ///     <paramref name="cacheRoot" />. <paramref name="activeTag" /> selects the recommended-pinned release by
    ///     default; pass a different tag to model a user-selected upgrade (the pinned tag's cache is never touched).
    ///     The optional <paramref name="catalog" /> + <paramref name="installedRuntimeStore" /> drive the 3-tier resolve
    ///     (live API → <c>installed-runtime.json</c> → pinned floor); when omitted (the test seam) only the pinned floor
    ///     is used, preserving the original behavior. The optional <paramref name="overrideOptions" /> carries the
    ///     operator bring-your-own override; when active, <see cref="EnsureBinaryAsync" /> validates and serves the
    ///     supplied binary instead of acquiring one.
    /// </summary>
    public LlamaCppBinaryManager(HttpClient httpClient,
        string? cacheRoot = null,
        string? activeTag = null,
        ILlamaCppReleaseCatalog? catalog = null,
        IInstalledRuntimeStore? installedRuntimeStore = null,
        LlamaServerRuntimeOverrideOptions? overrideOptions = null,
        ICudaManagedBuildSignal? managedCudaSignal = null)
        : this(httpClient,
            cacheRoot ?? DefaultCacheRoot(),
            activeTag ?? LlamaCppReleasePins.PinnedTag,
            CurrentOsPlatform(),
            RuntimeInformation.ProcessArchitecture,
            catalog,
            installedRuntimeStore,
            overrideOptions,
            managedCudaSignal)
    {
    }

    /// <summary>Test seam: pins OS/arch so asset selection can be exercised on any host.</summary>
    internal LlamaCppBinaryManager(HttpClient httpClient,
        string cacheRoot,
        string activeTag,
        OSPlatform os,
        Architecture arch,
        ILlamaCppReleaseCatalog? catalog = null,
        IInstalledRuntimeStore? installedRuntimeStore = null,
        LlamaServerRuntimeOverrideOptions? overrideOptions = null,
        ICudaManagedBuildSignal? managedCudaSignal = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeTag);
        _cacheRoot = cacheRoot;
        _activeTag = activeTag;
        _os = os;
        _arch = arch;
        _catalog = catalog;
        _installedRuntimeStore = installedRuntimeStore;
        _overrideOptions = overrideOptions;
        _managedCudaSignal = managedCudaSignal;
    }

    /// <inheritdoc />
    public async Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct)
    {
        // Operator bring-your-own override: an active override short-circuits ALL acquisition (no download, no cache write,
        // no installed-runtime.json mutation). The supplied binary is validated and served as the override's OWN variant —
        // never the caller-passed variant. A configured-but-broken override throws a sanitized failure rather than falling
        // through to acquisition or a silent CPU run. This precedes the pinned-floor resolve below, which would otherwise
        // throw for a (Linux, X64, Cuda) request that has no prebuilt asset.
        if (_overrideOptions?.IsActive == true)
        {
            return await ResolveOverrideBinaryAsync(_overrideOptions, ct).ConfigureAwait(false);
        }

        // Tier 1: a live-resolvable recommended runtime takes precedence. The installed-runtime state (tier 2) records
        // which tag is actually on disk; the pinned floor (tier 3) is the offline last-resort and the asset-name
        // template source. A catalog/state-store absence (test seam) collapses straight to the pinned floor.
        var installed = _installedRuntimeStore is null
            ? null
            : await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);

        // Managed source-built CUDA short-circuit: a Cuda request with a recorded source build serves the locally-built
        // binary instead of an acquisition (upstream ships no Linux CUDA prebuilt, so the pin resolve below would throw).
        // Re-validated on EVERY serve (full path-chain perms + recorded-SHA256 recompare) so an adopt→restart→serve TOCTOU
        // or a deep-tree swap is caught. A recorded-but-missing/invalid build clears the record + signal and falls through
        // to the normal path (which throws the sanitized "no prebuilt for this OS/arch" for a Cuda request — never a
        // silent CPU serve). Reuses the already-read `installed`, so no extra store I/O.
        if (installed?.SourceBuildPath is { Length: > 0 } && installed.Variant == variant)
        {
            var managed = await TryServeManagedSourceBinaryAsync(installed, ct).ConfigureAwait(false);
            if (managed is not null)
            {
                return managed;
            }
        }

        var resolvedTag = await ResolveActiveTagAsync(variant, installed, ct).ConfigureAwait(false);

        // Resolve the pin for the requested variant. A GPU variant (Cuda/Vulkan) MUST resolve to a GENUINE
        // (os, arch, variant) asset via TryResolveExact — Resolve() would substitute the CPU floor when no GPU prebuilt
        // exists (e.g. Linux CUDA has none upstream), and serving that CPU archive as a GPU LlamaBinary would make the
        // supervisor emit GPU placement flags against a CPU build (GPTAUD-09b). A missing GPU prebuilt therefore throws
        // the sanitized no-prebuilt error rather than falling through to CPU. The managed source-built CUDA short-circuit
        // above already served any valid local CUDA build, so reaching here for a Cuda request means none was usable. The
        // CPU variant keeps the plain Resolve (its exact pin IS the CPU floor).
        var pin = (variant == GpuVariant.Cpu
                      ? LlamaCppReleasePins.Resolve(_os, _arch, variant)
                      : LlamaCppReleasePins.TryResolveExact(_os, _arch, variant))
                  ?? throw new LlamaRuntimeException("No prebuilt llama.cpp runtime is available for this operating system and CPU architecture.");

        var isPinnedFallback = string.Equals(resolvedTag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal);
        var variantDir = Path.Combine(_cacheRoot, "llama.cpp", resolvedTag, VariantSlug(variant));

        // Offline / already-cached path: reuse a present binary without re-download.
        var cachedServer = ResolveServerPath(variantDir, pin);
        if (cachedServer is not null)
        {
            // Idempotent: when the cudart DLLs are already present next to the server this is a no-op; a cached CUDA dir
            // that is somehow missing them gets them topped up from the pinned companion before the binary is served.
            await EnsureCudartRuntimeAsync(resolvedTag, pin, cudartAsset: null, variant, variantDir, cachedServer, ct).ConfigureAwait(false);
            await RecordResolvedRuntimeAsync(resolvedTag, pin, variant, installed, ct).ConfigureAwait(false);
            return new LlamaBinary(cachedServer, resolvedTag, variant, isPinnedFallback);
        }

        // The pinned path has no catalog-reported size — pass "unknown" (0) so only the absolute ceiling is enforced.
        await DownloadVerifyExtractAsync(LlamaCppReleasePins.DownloadUri(resolvedTag, pin.AssetName), pin.AssetName, pin.Sha256, expectedSize: 0, variantDir, ct).ConfigureAwait(false);

        var serverPath = ResolveServerPath(variantDir, pin);
        if (serverPath is null)
        {
            throw new LlamaRuntimeException("The downloaded llama.cpp runtime did not contain the expected server executable.");
        }

        // Pair the CUDA runtime DLLs (pinned companion) before the binary is recorded/served — a CUDA build without its
        // cudart archive silently degrades to CPU-only. A cudart failure deletes the half-CUDA variant dir and throws.
        await EnsureCudartRuntimeAsync(resolvedTag, pin, cudartAsset: null, variant, variantDir, serverPath, ct).ConfigureAwait(false);

        await RecordResolvedRuntimeAsync(resolvedTag, pin, variant, installed, ct).ConfigureAwait(false);
        return new LlamaBinary(serverPath, resolvedTag, variant, isPinnedFallback);
    }

    /// <summary>
    ///     Records the runtime that <see cref="EnsureBinaryAsync" /> actually resolved on disk into
    ///     <see cref="IInstalledRuntimeStore" /> so a pin-bootstrapped / cached binary surfaces as "Installed" on first
    ///     load — without ever having gone through an explicit <see cref="InstallTagAsync" />.
    ///     <para>
    ///         <b>Record-integrity invariant:</b> the asset name and SHA256 written here come from <paramref name="pin" />,
    ///         which is resolved purely by OS/arch/<paramref name="variant" /> — it carries the PINNED-floor asset/digest,
    ///         NOT the asset/digest of an arbitrary <paramref name="resolvedTag" />. They are therefore truthful ONLY when
    ///         the resolve actually landed on the pinned floor. So this records exclusively when
    ///         <c>resolvedTag == PinnedTag</c>: that is the one bootstrap case where no <see cref="InstallTagAsync" /> ever
    ///         ran yet the binary is on disk. A non-pinned <paramref name="resolvedTag" /> necessarily originated from an
    ///         existing <see cref="InstallTagAsync" /> record (the only writer of a non-pinned tag) — that record already
    ///         holds the correct asset/digest, so there is nothing to fill and writing the pin's values would corrupt it.
    ///     </para>
    ///     Cheap on the hot path: a write happens only on the first pinned-floor ensure with no matching record; a record
    ///     that already pins the same (tag, variant) is left untouched, so a steady-state ensure never rewrites.
    /// </summary>
    private async Task RecordResolvedRuntimeAsync(string resolvedTag, LlamaCppAssetPin pin, GpuVariant variant, InstalledRuntimeState? installed, CancellationToken ct)
    {
        if (_installedRuntimeStore is null)
        {
            return;
        }

        // The source-build record is immutable to this bootstrap writer: a non-Cuda EnsureBinaryAsync (e.g. the Vulkan
        // probe at LlamaListDevicesVramProbe) must NEVER overwrite an adopted managed CUDA build. The variant skip below
        // does not fire for a Cuda record + a Vulkan write, so guard the source build explicitly first. [archHIGH-1]
        if (installed?.SourceBuildPath is { Length: > 0 })
        {
            return;
        }

        // Only the pinned floor carries a tag whose asset/digest match the pin we are about to record. A non-pinned
        // resolvedTag came from an existing InstallTagAsync record (already correct) — never overwrite it with pin data.
        if (!string.Equals(resolvedTag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal))
        {
            return;
        }

        // A record already exists for this variant. Whether it pins the same tag (steady state) or a newer
        // explicitly-installed tag, the pinned floor must not overwrite it — only the live/tier-2 resolve advances a
        // record, and that path is excluded above. So a record for this variant is left untouched.
        if (installed is { } current && current.Variant == variant)
        {
            return;
        }

        var state = new InstalledRuntimeState(resolvedTag, pin.AssetName, pin.Sha256, variant, DateTimeOffset.UtcNow);
        await _installedRuntimeStore.WriteAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     3-tier resolve of the tag to acquire: the ctor's <c>_activeTag</c> is the floor (pinned). When a catalog is
    ///     present, a live-confirmed recommended tag wins; otherwise the on-disk installed tag (tier 2) is used when
    ///     present. Offline/rate-limited live lookups fall through silently — acquisition never depends on the network.
    /// </summary>
    private async Task<string> ResolveActiveTagAsync(GpuVariant variant, InstalledRuntimeState? installed, CancellationToken ct)
    {
        if (_catalog is not null && IsValidTag(_activeTag))
        {
            var live = await _catalog.ResolveAssetAsync(_activeTag, _os, _arch, variant, ct).ConfigureAwait(false);
            if (live is { IsOffline: false, IsRateLimited: false } && live.Tag is { Length: > 0 } liveTag)
            {
                return liveTag;
            }
        }

        // Tier 2: the recorded installed tag, when present (and only when it is not already the floor request).
        if (installed is { Tag.Length: > 0 } && IsValidTag(installed.Tag))
        {
            return installed.Tag;
        }

        // Tier 3: the pinned floor — the original behavior, including a brand-new offline first run.
        return _activeTag;
    }

    /// <inheritdoc />
    public async Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        if (!IsValidTag(tag))
        {
            throw new LlamaRuntimeException("The requested llama.cpp runtime version is not in a recognized format.");
        }

        // The asset name is interpolated into a temp file path and the download URL — it comes from the live GitHub API,
        // so gate it against a strict allow-list (no path/URL metacharacters) before it touches either.
        if (!IsValidAssetName(assetName))
        {
            throw new LlamaRuntimeException("The requested llama.cpp runtime asset name is not in a recognized format.");
        }

        var expectedDigest = StripDigestPrefix(digestSha256);
        if (expectedDigest.Length != 64 || !expectedDigest.All(Uri.IsHexDigit))
        {
            throw new LlamaRuntimeException("The requested llama.cpp runtime could not be verified (missing integrity digest).");
        }

        // Disk-exhaustion guard: a catalog-reported size beyond the absolute ceiling is rejected before any download.
        if (expectedSize > MaxDownloadBytes)
        {
            throw new LlamaRuntimeException("The requested llama.cpp runtime is larger than the maximum allowed download size.");
        }

        var variantDir = Path.Combine(_cacheRoot, "llama.cpp", tag, VariantSlug(variant));
        var url = LlamaCppReleasePins.DownloadUri(tag, assetName);

        // Reuse the shared download→verify→atomic-extract pipeline, verifying against the live publisher digest. On any
        // failure the previously-installed binary (a sibling versioned dir) is untouched — versioned dirs isolate tiers.
        await DownloadVerifyExtractAsync(url, assetName, expectedDigest, expectedSize, variantDir, ct).ConfigureAwait(false);

        var pin = LlamaCppReleasePins.Resolve(_os, _arch, variant);
        var serverPath = ResolveServerPathForAsset(variantDir, pin);
        if (serverPath is null)
        {
            throw new LlamaRuntimeException("The downloaded llama.cpp runtime did not contain the expected server executable.");
        }

        // Pair the CUDA runtime DLLs (live companion) BEFORE the smoke test so the self-check exercises a complete CUDA
        // install. The companion name is derived from the resolved main asset and its digest is resolved live the same
        // way the main asset's was. A cudart failure deletes the half-CUDA variant dir and throws (never install blind).
        await EnsureCudartRuntimeAsync(tag, pin, cudartAsset: assetName, variant, variantDir, serverPath, ct).ConfigureAwait(false);

        // Smoke test BEFORE recording the install: a binary that cannot even report its version is not made active. A
        // failed self-check must not leave a half-validated variant dir on disk where a later EnsureBinaryAsync tier-1
        // resolve could serve it unverified — best-effort delete it before surfacing the failure.
        if (!await SmokeTestAsync(serverPath, ct).ConfigureAwait(false))
        {
            TryDeleteDirectory(variantDir);
            throw new LlamaRuntimeException("The downloaded llama.cpp runtime failed its post-install self-check.");
        }

        if (_installedRuntimeStore is not null)
        {
            var state = new InstalledRuntimeState(tag, assetName, expectedDigest, variant, DateTimeOffset.UtcNow);
            await _installedRuntimeStore.WriteAsync(state, ct).ConfigureAwait(false);
        }

        return new LlamaBinary(serverPath, tag, variant, IsPinnedFallback: false);
    }

    /// <summary>
    ///     Pairs the Windows-CUDA runtime DLLs (<c>cudart64_*.dll</c>, <c>cublas64_*.dll</c>, <c>cublasLt64_*.dll</c>) next
    ///     to <c>llama-server.exe</c>. llama.cpp ships these in a SEPARATE archive from the main CUDA build; without them
    ///     the ggml-cuda backend fails to load and the server silently runs CPU-only. No-op for every non-Windows-CUDA
    ///     acquisition. Idempotent: if the DLLs already sit next to the server (cached-dir reuse) nothing is downloaded.
    ///     <para>
    ///         <paramref name="cudartAsset" /> selects the digest source: <see langword="null" /> (the pinned/cached path)
    ///         uses the pin's companion name + sha; a non-null value (the live <see cref="InstallTagAsync" /> path) is the
    ///         resolved MAIN asset name from which the cudart name is derived and whose digest is resolved live the SAME
    ///         way the main asset's was. When the live digest cannot be resolved this throws rather than installing a CUDA
    ///         build without its runtime (which reproduces the silent-CPU bug). A fetch/verify failure deletes the
    ///         half-CUDA <paramref name="variantDir" /> so a later resolve cannot serve it as a valid CUDA install.
    ///     </para>
    /// </summary>
    private async Task EnsureCudartRuntimeAsync(string tag, LlamaCppAssetPin? pin, string? cudartAsset, GpuVariant variant, string variantDir, string serverPath, CancellationToken ct)
    {
        // Windows-CUDA only — Vulkan/CPU/Linux need no second archive and must be byte-unchanged.
        if (variant != GpuVariant.Cuda || _os != OSPlatform.Windows)
        {
            return;
        }

        var serverDir = Path.GetDirectoryName(serverPath);
        if (string.IsNullOrEmpty(serverDir))
        {
            throw new LlamaRuntimeException("The llama.cpp CUDA runtime could not be installed (server directory is unresolved).");
        }

        // Idempotency: the cudart core DLL already next to the server means a hash-valid CUDA dir is being reused — skip.
        if (CudartRuntimePresent(serverDir))
        {
            return;
        }

        // Resolve the companion archive name + expected digest. Pinned/cached path: the pin row carries both. Live path:
        // derive the name from the resolved main asset and resolve its digest from the live release-assets API.
        string cudartName;
        string cudartDigest;
        long cudartSize;
        if (cudartAsset is null)
        {
            if (pin?.CudartAssetName is not { Length: > 0 } pinnedName || pin.CudartSha256 is not { Length: > 0 } pinnedSha)
            {
                throw new LlamaRuntimeException("The pinned llama.cpp CUDA runtime is missing its companion runtime archive metadata.");
            }

            cudartName = pinnedName;
            cudartDigest = pinnedSha;
            cudartSize = 0;
        }
        else
        {
            var derived = LlamaCppReleasePins.DeriveCudartAssetName(cudartAsset);
            if (derived is null)
            {
                throw new LlamaRuntimeException("The llama.cpp CUDA runtime companion archive name could not be derived.");
            }

            var companion = _catalog is null
                ? null
                : await _catalog.ResolveCompanionAssetAsync(tag, derived, ct).ConfigureAwait(false);
            if (companion?.Asset is not { } asset)
            {
                // No live digest → fail clearly. Installing the CUDA build without its runtime reproduces the silent-CPU bug.
                throw new LlamaRuntimeException("The llama.cpp CUDA runtime could not be verified (its companion runtime archive is unavailable).");
            }

            cudartName = asset.Name;
            cudartDigest = asset.Digest;
            cudartSize = asset.Size;
        }

        try
        {
            await DownloadVerifyFlattenCudartAsync(LlamaCppReleasePins.DownloadUri(tag, cudartName), cudartName, cudartDigest, cudartSize, serverDir, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A half-CUDA dir (main archive extracted, runtime DLLs missing) must not survive to look like a valid CUDA
            // install on a later resolve — discard it so the next acquisition re-runs the complete pairing.
            TryDeleteDirectory(variantDir);
            throw;
        }

        if (!CudartRuntimePresent(serverDir))
        {
            TryDeleteDirectory(variantDir);
            throw new LlamaRuntimeException("The llama.cpp CUDA runtime archive did not contain the expected runtime libraries.");
        }
    }

    /// <summary>True when the core CUDA runtime DLL is present next to the server (the pairing has already happened).</summary>
    private static bool CudartRuntimePresent(string serverDir)
    {
        return Directory.Exists(serverDir)
               && Directory.EnumerateFiles(serverDir, "cudart64_*.dll", SearchOption.TopDirectoryOnly).Any();
    }

    /// <summary>
    ///     Download → size-check → SHA256-verify the cudart archive, then FLATTEN its DLLs into the server's bin dir
    ///     (regardless of their internal nesting) so the OS loader finds them next to <c>llama-server.exe</c>. Retried
    ///     exactly once on a transient failure / hash mismatch, mirroring the main archive pipeline.
    /// </summary>
    private async Task DownloadVerifyFlattenCudartAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string serverDir, CancellationToken ct)
    {
        var firstError = await TryDownloadVerifyFlattenCudartAsync(url, assetName, expectedSha256, expectedSize, serverDir, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await TryDownloadVerifyFlattenCudartAsync(url, assetName, expectedSha256, expectedSize, serverDir, ct).ConfigureAwait(false);
        if (secondError is null)
        {
            return;
        }

        throw new LlamaRuntimeException("The llama.cpp CUDA runtime archive could not be downloaded or failed integrity verification after a retry.",
            secondError);
    }

    private async Task<Exception?> TryDownloadVerifyFlattenCudartAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string serverDir, CancellationToken ct)
    {
        var tempArchive = Path.Combine(Path.GetTempPath(), $"llamacpp-cudart-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        var stagingDir = Path.Combine(Path.GetTempPath(), $"llamacpp-cudart-{Guid.NewGuid():N}");
        try
        {
            await DownloadToFileAsync(url, tempArchive, expectedSize, ct).ConfigureAwait(false);

            if (expectedSize > 0 && new FileInfo(tempArchive).Length != expectedSize)
            {
                return new LlamaRuntimeException("The llama.cpp CUDA runtime archive did not match its expected size.");
            }

            if (!await HashMatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return new LlamaRuntimeException("The llama.cpp CUDA runtime archive failed integrity verification.");
            }

            // Extract to a temp staging dir, then flatten only the runtime DLLs into the server dir — a partial extract
            // never touches the live server dir, and the archive's internal nesting (root or build/bin) is irrelevant.
            Directory.CreateDirectory(stagingDir);
            await ZipFile.ExtractToDirectoryAsync(tempArchive, stagingDir, ct).ConfigureAwait(false);
            FlattenDllsInto(stagingDir, serverDir);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            TryDeleteFile(tempArchive);
            TryDeleteDirectory(stagingDir);
        }
    }

    /// <summary>Copies every <c>*.dll</c> found anywhere under <paramref name="sourceRoot" /> into <paramref name="serverDir" /> (flattened, overwriting).</summary>
    private static void FlattenDllsInto(string sourceRoot, string serverDir)
    {
        Directory.CreateDirectory(serverDir);
        foreach (var dll in Directory.EnumerateFiles(sourceRoot, "*.dll", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(serverDir, Path.GetFileName(dll));
            File.Copy(dll, destination, overwrite: true);
        }
    }

    /// <summary>
    ///     Spawns the resolved <c>llama-server</c> with <c>--version</c> and a short timeout. A clean exit (or any
    ///     version banner output) is a pass; a non-zero hard failure, a launch failure, or a timeout is a fail. The
    ///     process is tree-killed on timeout so no orphan lingers.
    /// </summary>
    private static async Task<bool> SmokeTestAsync(string serverPath, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo(serverPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(serverPath) ?? Environment.CurrentDirectory
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(SmokeTestTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                TryKill(process);
                return false;
            }

            // llama-server --version prints its banner and exits 0; some builds exit non-zero but still print a banner.
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A launch failure (missing exec bit, wrong arch, missing GPU runtime) is a failed self-check, not a crash.
            return false;
        }
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
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — nothing to do.
        }
        catch (NotSupportedException)
        {
            // Platform without tree-kill support — best effort.
        }
    }

    /// <summary>
    ///     Locates the <c>llama-server</c> executable inside an extracted variant directory. The pinned relative path
    ///     is tried first (fast path); if the upstream archive layout differs from the pin — llama.cpp release archives
    ///     have shipped the binary under both <c>build/bin/</c> and a top-level <c>llama-{tag}/</c> folder — fall back
    ///     to a recursive search by file name so an upstream layout change does not silently break acquisition. Returns
    ///     <see langword="null" /> when no executable of that name exists under the directory.
    /// </summary>
    private static string? ResolveServerPath(string variantDir, LlamaCppAssetPin pin)
    {
        return ResolveServerPathByName(variantDir, pin.ServerRelativePath);
    }

    /// <summary>
    ///     Locates the server executable for a dynamically-installed asset. When a pin for the host exists its relative
    ///     path/name is used; otherwise the OS-appropriate default server name is searched for. Tolerant of upstream
    ///     archive layout drift via the recursive tree search.
    /// </summary>
    private string? ResolveServerPathForAsset(string variantDir, LlamaCppAssetPin? pin)
    {
        var relative = pin?.ServerRelativePath
                       ?? (_os == OSPlatform.Windows ? "build/bin/llama-server.exe" : "build/bin/llama-server");
        return ResolveServerPathByName(variantDir, relative);
    }

    private static string? ResolveServerPathByName(string variantDir, string serverRelativePath)
    {
        var pinned = Path.GetFullPath(Path.Combine(variantDir, serverRelativePath));
        if (File.Exists(pinned))
        {
            return pinned;
        }

        if (!Directory.Exists(variantDir))
        {
            return null;
        }

        var serverFileName = Path.GetFileName(serverRelativePath);
        return Directory
               .EnumerateFiles(variantDir, serverFileName, SearchOption.AllDirectories)
               .FirstOrDefault();
    }

    /// <summary>
    ///     Shared download → SHA256-verify → atomic-extract pipeline. The expected digest is supplied by the caller —
    ///     the pinned hash (<see cref="EnsureBinaryAsync" />) or the live publisher digest
    ///     (<see cref="InstallTagAsync" />) — so both acquisition paths run identical verification logic. A transient
    ///     failure or a hash mismatch is discarded and retried exactly once.
    /// </summary>
    private async Task DownloadVerifyExtractAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string variantDir, CancellationToken ct)
    {
        var firstError = await TryDownloadVerifyExtractAsync(url, assetName, expectedSha256, expectedSize, variantDir, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await TryDownloadVerifyExtractAsync(url, assetName, expectedSha256, expectedSize, variantDir, ct).ConfigureAwait(false);
        if (secondError is null)
        {
            return;
        }

        throw new LlamaRuntimeException("The llama.cpp runtime could not be downloaded or failed integrity verification after a retry. "
                                        + "Check the network connection and try again.",
            secondError);
    }

    /// <summary>
    ///     Runs one download → SHA256 verify → extract pass. Returns <see langword="null" /> on success, or the
    ///     non-fatal failure cause to drive a single retry. Cancellation propagates rather than being swallowed.
    /// </summary>
    private async Task<Exception?> TryDownloadVerifyExtractAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string variantDir, CancellationToken ct)
    {
        // Defense-in-depth: even though assetName is allow-list-validated upstream, strip any directory component before
        // it composes a temp path so a future caller can never traverse out of the temp dir.
        var tempArchive = Path.Combine(Path.GetTempPath(), $"llamacpp-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        try
        {
            await DownloadToFileAsync(url, tempArchive, expectedSize, ct).ConfigureAwait(false);

            // When the catalog reported a size, the on-disk length must match it exactly before we trust+hash the file.
            if (expectedSize > 0 && new FileInfo(tempArchive).Length != expectedSize)
            {
                return new LlamaRuntimeException("The llama.cpp runtime download did not match its expected size.");
            }

            if (!await HashMatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return new LlamaRuntimeException("The llama.cpp runtime download failed integrity verification.");
            }

            ExtractArchive(tempArchive, assetName, variantDir);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            TryDeleteFile(tempArchive);
        }
    }

    private async Task DownloadToFileAsync(Uri url, string destination, long expectedSize, CancellationToken ct)
    {
        using var response = await _httpClient
                                   .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                   .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Bound the write: when a size is known, allow it plus a small slack; otherwise fall back to the absolute
        // ceiling. A stream that exceeds the bound (a hostile/buggy server) is aborted and the partial file discarded.
        var limit = expectedSize > 0
            ? Math.Min(expectedSize + DownloadSizeSlackBytes, MaxDownloadBytes)
            : MaxDownloadBytes;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        try
        {
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > limit)
                {
                    throw new LlamaRuntimeException("The llama.cpp runtime download exceeded the maximum allowed size.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDeleteFile(destination);
            throw;
        }
    }

    private static async Task<bool> HashMatchesAsync(string filePath, string expectedSha256, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(hash);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractArchive(string archivePath, string assetName, string variantDir)
    {
        // Extract into a temp sibling then atomically move into place so a partial extract can't masquerade as cached.
        var stagingDir = $"{variantDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, stagingDir);
            }
            else if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                ExtractTarGz(archivePath, stagingDir);
            }
            else
            {
                throw new LlamaRuntimeException("The llama.cpp runtime archive format is not supported.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(variantDir.TrimEnd(Path.DirectorySeparatorChar))!);
            if (Directory.Exists(variantDir))
            {
                Directory.Delete(variantDir, recursive: true);
            }

            Directory.Move(stagingDir, variantDir);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
    }

    private static void ExtractTarGz(string archivePath, string destination)
    {
        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp download; ignore.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp download; ignore.
        }
    }

    /// <summary>Validates a release tag against the upstream <c>b&lt;N&gt;</c> scheme before it is composed into a URL.</summary>
    private static bool IsValidTag(string? tag)
    {
        return !string.IsNullOrWhiteSpace(tag) && TagRegex().IsMatch(tag);
    }

    /// <summary>
    ///     Allow-list gate on a release asset name (a live-GitHub value) before it is interpolated into a temp file path or
    ///     the download URL. Only the file-name alphabet is permitted — no path/URL separators or <c>..</c> segments.
    /// </summary>
    private static bool IsValidAssetName(string? assetName)
    {
        return !string.IsNullOrWhiteSpace(assetName)
               && !assetName.Contains("..", StringComparison.Ordinal)
               && AssetNameRegex().IsMatch(assetName);
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
            // Best-effort cleanup of a failed install; never mask the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a failed install; never mask the original failure.
        }
    }

    /// <summary>
    ///     Strips a leading <c>sha256:</c> prefix (if present) from a publisher digest. Case is preserved — the hash
    ///     comparison in <see cref="HashMatchesAsync" /> is already case-insensitive, so no case folding is needed.
    /// </summary>
    private static string StripDigestPrefix(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return string.Empty;
        }

        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        return value;
    }

    [GeneratedRegex(@"^b[0-9]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AssetNameRegex();

    private static string VariantSlug(GpuVariant variant)
    {
        return variant switch
        {
            GpuVariant.Cuda => "cuda",
            GpuVariant.Vulkan => "vulkan",
            _ => "cpu"
        };
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine");
    }

    /// <summary>
    ///     The directory every acquired llama.cpp runtime is cached under for the default app-data root
    ///     (<c>{cacheRoot}/llama.cpp</c>, the same layout <see cref="EnsureBinaryAsync" /> writes its variant dirs into).
    ///     Exposed so the startup orphan reaper matches ONLY <c>llama-server</c> binaries this app acquired, never an
    ///     unrelated install.
    /// </summary>
    internal static string DefaultLlamaCppBinariesRoot()
    {
        return Path.Combine(DefaultCacheRoot(), "llama.cpp");
    }

    private static OSPlatform CurrentOsPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return OSPlatform.OSX;
        }

        return OSPlatform.Linux;
    }
}
