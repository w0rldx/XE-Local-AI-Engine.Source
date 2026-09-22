namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Default <see cref="ILlamaCppBinaryManager" />: resolves the pinned prebuilt asset for the host, downloads it
///     over HTTP, verifies its SHA256 against <see cref="LlamaCppReleasePins" />, extracts it under a stable cache
///     directory, and returns the resolved <c>llama-server</c> path. Never source-builds.
/// </summary>
/// <remarks>
///     Cache layout <c>{cacheRoot}/llama.cpp/{tag}/{variant}/</c> holds the extracted archive: a hash-valid cached
///     binary is reused without re-download (the offline path), and a user-selected upgrade caches under its own
///     <c>{tag}</c> directory so it never deletes the recommended-pinned fallback. On SHA256 mismatch the partial
///     download is discarded and retried <em>once</em>; a second mismatch surfaces a sanitized
///     <see cref="LlamaRuntimeException" /> carrying no internal paths or URLs.
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore is a process-lifetime singleton coordination primitive and is never disposed while provider operations may still be active.")]
public sealed partial class LlamaCppBinaryManager : ILlamaCppBinaryManager
{
    private static readonly TimeSpan SmokeTestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Absolute hard ceiling on a single runtime download: a disk-exhaustion guard against a hostile or buggy server
    ///     streaming an unbounded body.
    /// </summary>
    /// <remarks>
    ///     A prebuilt llama.cpp asset is well under this. Enforced on every download path; the size-aware
    ///     <see cref="InstallTagAsync" /> path tightens it further with the catalog-reported size.
    /// </remarks>
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Slack added to the catalog-reported size before aborting an oversized stream (still capped at the ceiling).</summary>
    private const long DownloadSizeSlackBytes = 1L * 1024 * 1024;

    /// <summary>Refusal for a prebuilt install while a source build owns the record — checked before AND after the download.</summary>
    private const string SourceBuildInstalledMessage =
        "Remove the installed source-built llama.cpp runtime before installing a prebuilt runtime.";

    private readonly IRuntimeAcquisitionStatusRegistry? _acquisitionStatus;
    private readonly string _activeTag;
    private readonly Architecture _arch;
    private readonly string _cacheRoot;
    private readonly ILlamaCppReleaseCatalog? _catalog;
    private readonly HttpClient _httpClient;
    private readonly IInstalledRuntimeStore? _installedRuntimeStore;
    private readonly ICudaManagedBuildSignal? _managedCudaSignal;
    private readonly OSPlatform _os;
    private readonly LlamaServerRuntimeOverrideOptions? _overrideOptions;
    private readonly SemaphoreSlim _sourceMutationGate = new(initialCount: 1, maxCount: 1);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    ///     Creates a binary manager that downloads through <paramref name="httpClient" />, caches under
    ///     <paramref name="cacheRoot" /> and reads the clock from <paramref name="timeProvider" />.
    /// </summary>
    /// <remarks>
    ///     <paramref name="activeTag" /> selects the recommended-pinned release by default; a different tag models a
    ///     user-selected upgrade, and the pinned tag's cache is never touched. <paramref name="catalog" /> and
    ///     <paramref name="installedRuntimeStore" /> drive the 3-tier resolve; omitted (the test seam), only the pinned
    ///     floor is used. An active <paramref name="overrideOptions" /> makes <see cref="EnsureBinaryAsync" /> validate
    ///     and serve the operator's own binary. <paramref name="acquisitionStatus" /> is the optional TRAILING progress side-channel; without it acquisition is identical and silent.
    /// </remarks>
    public LlamaCppBinaryManager(HttpClient httpClient,
        TimeProvider timeProvider,
        string? cacheRoot = null,
        string? activeTag = null,
        ILlamaCppReleaseCatalog? catalog = null,
        IInstalledRuntimeStore? installedRuntimeStore = null,
        LlamaServerRuntimeOverrideOptions? overrideOptions = null,
        ICudaManagedBuildSignal? managedCudaSignal = null,
        IRuntimeAcquisitionStatusRegistry? acquisitionStatus = null)
        : this(httpClient,
            cacheRoot ?? RuntimeCacheDirectory.Resolve(),
            activeTag ?? LlamaCppReleasePins.PinnedTag,
            CurrentOsPlatform(),
            RuntimeInformation.ProcessArchitecture,
            timeProvider,
            catalog,
            installedRuntimeStore,
            overrideOptions,
            managedCudaSignal,
            acquisitionStatus)
    {
    }

    /// <summary>Test seam: pins OS/arch so asset selection can be exercised on any host.</summary>
    internal LlamaCppBinaryManager(HttpClient httpClient,
        string cacheRoot,
        string activeTag,
        OSPlatform os,
        Architecture arch,
        TimeProvider timeProvider,
        ILlamaCppReleaseCatalog? catalog = null,
        IInstalledRuntimeStore? installedRuntimeStore = null,
        LlamaServerRuntimeOverrideOptions? overrideOptions = null,
        ICudaManagedBuildSignal? managedCudaSignal = null,
        IRuntimeAcquisitionStatusRegistry? acquisitionStatus = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeTag);
        _cacheRoot = cacheRoot;
        _activeTag = activeTag;
        _os = os;
        _arch = arch;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _catalog = catalog;
        _installedRuntimeStore = installedRuntimeStore;
        _overrideOptions = overrideOptions;
        _managedCudaSignal = managedCudaSignal;
        _acquisitionStatus = acquisitionStatus;
    }

    /// <inheritdoc />
    public async Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct)
    {
        // An active operator bring-your-own override short-circuits ALL acquisition and is served as the override's OWN variant, never the caller's; broken, it
        // throws rather than run silently on CPU. It precedes the pinned-floor resolve, which would throw for a (Linux, X64, Cuda) request that has no prebuilt.
        if (_overrideOptions?.IsActive == true)
        {
            return await ResolveOverrideBinaryAsync(_overrideOptions, ct).ConfigureAwait(false);
        }

        // Tier 1 is a live-resolvable recommended runtime; the installed-runtime state (tier 2) records which tag is actually on disk; the pinned floor (tier 3) is
        // the offline last-resort and the asset-name template source. A catalog or state-store absence (test seam) collapses straight to the pinned floor.
        var installed = _installedRuntimeStore is null
            ? null
            : await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);

        // A recorded source build is AUTHORITATIVE and re-validated on EVERY serve (path-chain perms plus a recorded-SHA256 recompare), reusing the already-read record.
        // It catches an adopt-restart-serve TOCTOU or a deep-tree swap; never fall through to a prebuilt while one is recorded, or a race silently replaces the runtime.
        if (installed?.SourceBuildPath is { Length: > 0 })
        {
            // A variant disagreement is evidence about the CALLER's selection, never about the build, so seed the signal from the authoritative record and serve the
            // recorded build; every later selection then agrees. Why not discard the record: wiki 03, "GPU variant selection".
            if (installed.Variant != variant)
            {
                _managedCudaSignal?.SetActive(installed.Variant);
            }

            var managed = await TryServeManagedSourceBinaryAsync(installed, discardInvalidRecord: true, ct).ConfigureAwait(false);
            if (managed is not null)
            {
                return managed;
            }

            throw new LlamaRuntimeException(ManagedSourceBuildUnavailableMessage);
        }

        var resolvedTag = await ResolveActiveTagAsync(variant, installed, ct).ConfigureAwait(false);

        // A GPU variant MUST resolve a GENUINE (os, arch, variant) asset via TryResolveExact: Resolve() would substitute the CPU floor where no GPU prebuilt exists
        // — Linux CUDA has none upstream — and the supervisor would emit GPU placement flags against a CPU build. CPU keeps the plain Resolve, its exact pin BEING the floor.
        var pin = (variant == GpuVariant.Cpu
                      ? LlamaCppReleasePins.Resolve(_os, _arch, variant)
                      : LlamaCppReleasePins.TryResolveExact(_os, _arch, variant))
                  ?? throw new LlamaRuntimeException("No prebuilt llama.cpp runtime is available for this operating system and CPU architecture.");

        var isPinnedFallback = string.Equals(resolvedTag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal);
        var variantDir = Path.Combine(_cacheRoot, "llama.cpp", resolvedTag, VariantSlug(variant));

        // Progress side-channel, armed now that the variant and tag are known. Windows CUDA fetches TWO archives (build plus cudart companion), so the step count
        // keeps the UI from running 0→100 % twice. The cached-serve branch is inside the segment (it can top up a missing cudart); the short-circuits above are not.
        var reporter = new AcquisitionReporter(_acquisitionStatus,
            variant,
            resolvedTag,
            stepCount: variant == GpuVariant.Cuda && _os == OSPlatform.Windows ? 2 : 1);

        try
        {
            // Offline / already-cached path: reuse a present binary without re-download.
            var cachedServer = ResolveServerPath(variantDir, pin);
            if (cachedServer is not null)
            {
                // Idempotent: when the cudart DLLs are already present next to the server this is a no-op; a cached CUDA dir
                // that is somehow missing them gets them topped up from the pinned companion before the binary is served.
                await EnsureCudartRuntimeAsync(resolvedTag, pin, cudartAsset: null, variant, variantDir, cachedServer, reporter, ct).ConfigureAwait(false);
                await RecordResolvedRuntimeAsync(resolvedTag, pin, variant, ct).ConfigureAwait(false);

                // Silent on a pure cache hit — this runs on every model spawn, so a Completed here would flood the hub.
                reporter.Complete();
                return new LlamaBinary { ServerExecutablePath = cachedServer, Version = resolvedTag, Variant = variant, IsPinnedFallback = isPinnedFallback };
            }

            // The pinned path has no catalog-reported size — pass "unknown" (0) so only the absolute ceiling is enforced.
            await DownloadVerifyExtractAsync(LlamaCppReleasePins.DownloadUri(resolvedTag, pin.AssetName), pin.AssetName, pin.Sha256, expectedSize: 0, variantDir, reporter, stepIndex: 1, ct)
                .ConfigureAwait(false);

            var serverPath = ResolveServerPath(variantDir, pin);
            if (serverPath is null)
            {
                throw new LlamaRuntimeException("The downloaded llama.cpp runtime did not contain the expected server executable.");
            }

            // Pair the CUDA runtime DLLs (pinned companion) before the binary is recorded/served — a CUDA build without its
            // cudart archive silently degrades to CPU-only. A cudart failure deletes the half-CUDA variant dir and throws.
            await EnsureCudartRuntimeAsync(resolvedTag, pin, cudartAsset: null, variant, variantDir, serverPath, reporter, ct).ConfigureAwait(false);

            await RecordResolvedRuntimeAsync(resolvedTag, pin, variant, ct).ConfigureAwait(false);

            // A NEW binary is on disk (the cached-serve branch above deliberately does not do this — it runs on every
            // model spawn): bump the stamp so the device audit re-probes instead of serving a memo taken mid-download.
            _managedCudaSignal?.NotifyBinaryChanged();
            reporter.Complete();
            return new LlamaBinary { ServerExecutablePath = serverPath, Version = resolvedTag, Variant = variant, IsPinnedFallback = isPinnedFallback };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A cancelled acquisition is not a failed one: SpawnCoreAsync passes a REQUEST-scoped token here, so a chat abandoned mid-download — or a shutdown firing
            // the first-run service's stopping token — would otherwise persist a terminal Failed and leave the banner stuck behind a retry attached to a non-failure.
            throw;
        }
        catch (Exception exception)
        {
            // The banner must land on a terminal state or it runs forever; the reason is sanitized at the reporter.
            reporter.Fail(exception);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant,
        ILlamaServerRuntimeMutationLease mutationLease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutationLease);
        return EnsureBinaryAsync(variant, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Mirrors <see cref="EnsureBinaryAsync(GpuVariant,CancellationToken)" />'s RESOLVE order — override, recorded
    ///     source build, cached prebuilt — and stops where that method would start acquiring: no HTTP request, no
    ///     directory created (the cache tree is only probed), no <c>installed-runtime.json</c> write (neither
    ///     <c>RecordResolvedRuntimeAsync</c> nor the source-record discard runs), and the live-catalog tier skipped, so
    ///     the answer describes what is installed NOW and can lag a later ensure. It DOES spawn the override's own bounded, tree-killed validation.
    /// </remarks>
    public async Task<LlamaBinary?> TryGetInstalledBinaryAsync(GpuVariant variant, CancellationToken ct)
    {
        if (_overrideOptions?.IsActive == true)
        {
            // Unchanged semantics: a configured-but-broken override throws its sanitized refusal. That is a deliberate,
            // operator-actionable decision the caller must surface, not a "nothing installed yet".
            return await ResolveOverrideBinaryAsync(_overrideOptions, ct).ConfigureAwait(false);
        }

        var installed = _installedRuntimeStore is null
            ? null
            : await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);

        if (installed?.SourceBuildPath is { Length: > 0 })
        {
            // Full re-validation (path chain + recorded SHA256), but discardInvalidRecord:false — a read-only lookup must
            // not rewrite installed-runtime.json. A record the next Ensure will discard simply reads as "not resolvable".
            return await TryServeManagedSourceBinaryAsync(installed, discardInvalidRecord: false, ct).ConfigureAwait(false);
        }

        // Tier 2 (the recorded installed tag) then tier 3 (the pinned floor). The live catalog tier is deliberately
        // skipped: it is a network call, and it only ever selects a tag to ACQUIRE — it cannot make a binary appear.
        var resolvedTag = installed is { Tag.Length: > 0 } && IsValidTag(installed.Tag) ? installed.Tag : _activeTag;

        var pin = variant == GpuVariant.Cpu
            ? LlamaCppReleasePins.Resolve(_os, _arch, variant)
            : LlamaCppReleasePins.TryResolveExact(_os, _arch, variant);
        if (pin is null)
        {
            // No prebuilt exists for this (os, arch, variant) — e.g. Linux CUDA. Nothing can be on disk under that name.
            return null;
        }

        var variantDir = Path.Combine(_cacheRoot, "llama.cpp", resolvedTag, VariantSlug(variant));

        // ResolveServerPath only probes the filesystem (File.Exists, then an enumerate guarded by Directory.Exists) and creates nothing. A cached CUDA dir missing
        // its cudart companion is NOT topped up here (that downloads); it reads as installed, exactly as the supervisor's own ensure will find it before spawning.
        var cachedServer = ResolveServerPath(variantDir, pin);
        return cachedServer is null
            ? null
            : new LlamaBinary { ServerExecutablePath = cachedServer, Version = resolvedTag, Variant = variant, IsPinnedFallback = string.Equals(resolvedTag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal) };
    }

    /// <summary>
    ///     Records the runtime <see cref="EnsureBinaryAsync" /> actually resolved on disk into
    ///     <see cref="IInstalledRuntimeStore" />, so a pin-bootstrapped or cached binary surfaces as "Installed" on
    ///     first load without ever having gone through an explicit <see cref="InstallTagAsync" />.
    /// </summary>
    /// <remarks>
    ///     <b>Record-integrity invariant:</b> the asset name and SHA256 written here come from <paramref name="pin" />,
    ///     resolved purely by OS, arch and <paramref name="variant" />, so they carry the PINNED-floor asset and digest
    ///     and are truthful ONLY when the resolve landed on that floor — hence the write happens exclusively for the
    ///     pinned tag, the one bootstrap case where the binary is on disk yet no <see cref="InstallTagAsync" /> ever ran.
    ///     A non-pinned tag came from an existing install record, the only writer of one, whose values the pin would corrupt.
    /// </remarks>
    private async Task RecordResolvedRuntimeAsync(string resolvedTag, LlamaCppAssetPin pin, GpuVariant variant, CancellationToken ct)
    {
        if (_installedRuntimeStore is null)
        {
            return;
        }

        await _sourceMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The gate above only orders THIS process, and installed-runtime.json sits under the shared user-level cache root: the read below and the write at the end
            // of this method are one cross-process critical section too, or a second node adopting a source build between them lands under this prebuilt write.
            using var recordLock = await _installedRuntimeStore.AcquireAsync(ct).ConfigureAwait(false);

            var current = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
            if (current?.SourceBuildPath is { Length: > 0 })
            {
                throw new LlamaRuntimeException(ManagedSourceBuildUnavailableMessage);
            }

            // Only the pinned floor carries a tag whose asset/digest match the pin we are about to record. A non-pinned
            // resolvedTag came from an existing InstallTagAsync record (already correct) — never overwrite it with pin data.
            if (!string.Equals(resolvedTag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal))
            {
                return;
            }

            // A record already exists for this variant, pinning either the same tag (steady state) or a newer explicitly-installed one, and the pinned floor must not
            // overwrite it — so a write happens only on a first pinned-floor ensure with no matching record, and a steady-state ensure never rewrites.
            if (current is { } existing && existing.Variant == variant)
            {
                return;
            }

            var state = new InstalledRuntimeState(resolvedTag, pin.AssetName, pin.Sha256, variant, _timeProvider.GetUtcNow());
            await _installedRuntimeStore.WriteAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _sourceMutationGate.Release();
        }
    }

    /// <summary>
    ///     3-tier resolve of the tag to acquire, with the ctor's <c>_activeTag</c> (the pinned floor) as tier 3.
    /// </summary>
    /// <remarks>
    ///     When a catalog is present a live-confirmed recommended tag wins; otherwise the on-disk installed tag (tier 2)
    ///     is used when present. Offline or rate-limited live lookups fall through silently — acquisition never depends
    ///     on the network.
    /// </remarks>
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
        await _sourceMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_installedRuntimeStore is not null
                && (await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false))?.SourceBuildPath is { Length: > 0 })
            {
                throw new LlamaRuntimeException(SourceBuildInstalledMessage);
            }

            return await InstallTagCoreAsync(tag, assetName, digestSha256, expectedSize, variant, ct).ConfigureAwait(false);
        }
        finally
        {
            _sourceMutationGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<LlamaBinary> InstallTagAsync(string tag,
        string assetName,
        string digestSha256,
        long expectedSize,
        GpuVariant variant,
        ILlamaServerRuntimeMutationLease mutationLease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutationLease);
        return InstallTagAsync(tag, assetName, digestSha256, expectedSize, variant, ct);
    }

    private async Task<LlamaBinary> InstallTagCoreAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct)
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

        // The operator-initiated upgrade reports through the same channel as the first-run acquisition: same archives, same time, and the banner is the only place
        // either becomes visible. Armed after the request validation above, which acquires nothing and so must stay silent.
        var reporter = new AcquisitionReporter(_acquisitionStatus,
            variant,
            tag,
            stepCount: variant == GpuVariant.Cuda && _os == OSPlatform.Windows ? 2 : 1);

        try
        {
            // Reuse the shared download→verify→atomic-extract pipeline, verifying against the live publisher digest. On any
            // failure the previously-installed binary (a sibling versioned dir) is untouched — versioned dirs isolate tiers.
            await DownloadVerifyExtractAsync(url, assetName, expectedDigest, expectedSize, variantDir, reporter, stepIndex: 1, ct).ConfigureAwait(false);

            var pin = LlamaCppReleasePins.Resolve(_os, _arch, variant);
            var serverPath = ResolveServerPathForAsset(variantDir, pin);
            if (serverPath is null)
            {
                throw new LlamaRuntimeException("The downloaded llama.cpp runtime did not contain the expected server executable.");
            }

            // Pair the CUDA runtime DLLs (live companion) BEFORE the smoke test so the self-check exercises a complete CUDA install. The companion name derives from
            // the resolved main asset and its digest resolves live the same way; a cudart failure deletes the half-CUDA variant dir and throws, never installs blind.
            await EnsureCudartRuntimeAsync(tag, pin, cudartAsset: assetName, variant, variantDir, serverPath, reporter, ct).ConfigureAwait(false);

            // Smoke test BEFORE recording the install: a binary that cannot even report its version is not made active, and a failed self-check must not leave a
            // half-validated variant dir where a later EnsureBinaryAsync tier-1 resolve could serve it unverified — best-effort delete it before surfacing.
            if (!await SmokeTestAsync(serverPath, ct).ConfigureAwait(false))
            {
                TryDeleteDirectory(variantDir);
                throw new LlamaRuntimeException("The downloaded llama.cpp runtime failed its post-install self-check.");
            }

            if (_installedRuntimeStore is not null)
            {
                // InstallTagAsync's top guard ran BEFORE a minutes-long download, and the lock is deliberately not held across it (that would block every other node
                // for the transfer), so re-check here under the record lock. The binary stays under its own versioned dir; only the record is refused, with a reason.
                using var recordLock = await _installedRuntimeStore.AcquireAsync(ct).ConfigureAwait(false);
                if ((await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false))?.SourceBuildPath is { Length: > 0 })
                {
                    throw new LlamaRuntimeException(SourceBuildInstalledMessage);
                }

                var state = new InstalledRuntimeState(tag, assetName, expectedDigest, variant, _timeProvider.GetUtcNow());
                await _installedRuntimeStore.WriteAsync(state, ct).ConfigureAwait(false);
            }

            // Same reason as EnsureBinaryAsync: the operator-initiated install replaced the binary the audit was memoized against.
            _managedCudaSignal?.NotifyBinaryChanged();
            reporter.Complete();
            return new LlamaBinary { ServerExecutablePath = serverPath, Version = tag, Variant = variant, IsPinnedFallback = false };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is not a failure — see the matching note in EnsureBinaryAsync.
            throw;
        }
        catch (Exception exception)
        {
            reporter.Fail(exception);
            throw;
        }
    }

    /// <summary>
    ///     Pairs the Windows-CUDA runtime DLLs (<c>cudart64_*.dll</c>, <c>cublas64_*.dll</c>, <c>cublasLt64_*.dll</c>)
    ///     next to <c>llama-server.exe</c>; a no-op for every non-Windows-CUDA acquisition, and idempotent.
    /// </summary>
    /// <remarks>
    ///     llama.cpp ships these in a SEPARATE archive from the main CUDA build; without them the ggml-cuda backend
    ///     fails to load and the server silently runs CPU-only. <paramref name="cudartAsset" /> selects the digest
    ///     source: <see langword="null" /> (the pinned or cached path) uses the pin's companion name and sha, a non-null
    ///     value (the live <see cref="InstallTagAsync" /> path) is the resolved MAIN asset name the cudart name derives
    ///     from, whose digest resolves live the SAME way; an unresolvable live digest throws rather than reproduce the silent-CPU bug.
    /// </remarks>
    private async Task EnsureCudartRuntimeAsync(string tag, LlamaCppAssetPin? pin, string? cudartAsset, GpuVariant variant, string variantDir, string serverPath, AcquisitionReporter? reporter,
        CancellationToken ct)
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

        // Idempotency: the cudart core DLL already next to the server means a hash-valid CUDA dir is being reused — skip. Like the non-Windows-CUDA return above it
        // leaves the reporter untouched (nothing is acquired, so nothing may be announced); this method is step 2 of 2 and reports under CudartStepIndex.
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
            await DownloadVerifyFlattenCudartAsync(LlamaCppReleasePins.DownloadUri(tag, cudartName), cudartName, cudartDigest, cudartSize, serverDir, reporter, ct).ConfigureAwait(false);
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
    ///     Downloads, size-checks and SHA256-verifies the cudart archive, then FLATTENS its DLLs into the server's bin
    ///     dir, whatever their internal nesting, so the OS loader finds them next to <c>llama-server.exe</c>.
    /// </summary>
    /// <remarks>Retried exactly once on a transient failure or hash mismatch, mirroring the main archive pipeline.</remarks>
    private async Task DownloadVerifyFlattenCudartAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string serverDir, AcquisitionReporter? reporter, CancellationToken ct)
    {
        var firstError = await TryDownloadVerifyFlattenCudartAsync(url, assetName, expectedSha256, expectedSize, serverDir, reporter, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await TryDownloadVerifyFlattenCudartAsync(url, assetName, expectedSha256, expectedSize, serverDir, reporter, ct).ConfigureAwait(false);
        if (secondError is null)
        {
            return;
        }

        throw new LlamaRuntimeException("The llama.cpp CUDA runtime archive could not be downloaded or failed integrity verification after a retry.",
            secondError);
    }

    private async Task<Exception?> TryDownloadVerifyFlattenCudartAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string serverDir, AcquisitionReporter? reporter,
        CancellationToken ct)
    {
        var tempArchive = Path.Combine(Path.GetTempPath(), $"llamacpp-cudart-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        var stagingDir = Path.Combine(Path.GetTempPath(), $"llamacpp-cudart-{Guid.NewGuid():N}");
        try
        {
            // Announced before the request so the UI narrates the wait for the response headers too, not just the body.
            reporter?.Report(RuntimeAcquisitionPhase.Downloading, CudartStepIndex);
            await DownloadToFileAsync(url, tempArchive, expectedSize, reporter, CudartStepIndex, ct).ConfigureAwait(false);

            if (expectedSize > 0 && new FileInfo(tempArchive).Length != expectedSize)
            {
                return new LlamaRuntimeException("The llama.cpp CUDA runtime archive did not match its expected size.");
            }

            reporter?.Report(RuntimeAcquisitionPhase.Verifying, CudartStepIndex);
            if (!await HashMatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return new LlamaRuntimeException("The llama.cpp CUDA runtime archive failed integrity verification.");
            }

            // Extract to a temp staging dir, then flatten only the runtime DLLs into the server dir — a partial extract
            // never touches the live server dir, and the archive's internal nesting (root or build/bin) is irrelevant.
            reporter?.Report(RuntimeAcquisitionPhase.Extracting, CudartStepIndex);
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
    ///     Spawns the resolved <c>llama-server</c> with <c>--version</c> and a short timeout: a clean exit is a pass, a
    ///     non-zero exit, a launch failure or a timeout is a fail.
    /// </summary>
    /// <remarks>The process is tree-killed on timeout so no orphan lingers.</remarks>
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
    ///     Locates the <c>llama-server</c> executable inside an extracted variant directory, or <see langword="null" />
    ///     when no executable of that name exists under it.
    /// </summary>
    /// <remarks>
    ///     The pinned relative path is tried first (the fast path); if the upstream archive layout differs from the pin
    ///     — llama.cpp release archives have shipped the binary under both <c>build/bin/</c> and a top-level
    ///     <c>llama-{tag}/</c> folder — a recursive search by file name follows, so an upstream layout change does not
    ///     silently break acquisition.
    /// </remarks>
    private static string? ResolveServerPath(string variantDir, LlamaCppAssetPin pin)
    {
        return ResolveServerPathByName(variantDir, pin.ServerRelativePath);
    }

    /// <summary>
    ///     Locates the server executable for a dynamically-installed asset: a pin for the host supplies the relative
    ///     path or name, otherwise the OS-appropriate default server name is searched for.
    /// </summary>
    /// <remarks>Tolerant of upstream archive layout drift via the recursive tree search.</remarks>
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
    ///     Shared download, SHA256-verify and atomic-extract pipeline; a transient failure or a hash mismatch is
    ///     discarded and retried exactly once.
    /// </summary>
    /// <remarks>
    ///     The expected digest is supplied by the caller — the pinned hash (<see cref="EnsureBinaryAsync" />) or the
    ///     live publisher digest (<see cref="InstallTagAsync" />) — so both acquisition paths run identical
    ///     verification logic.
    /// </remarks>
    private async Task DownloadVerifyExtractAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string variantDir, AcquisitionReporter? reporter, int stepIndex,
        CancellationToken ct)
    {
        var firstError = await TryDownloadVerifyExtractAsync(url, assetName, expectedSha256, expectedSize, variantDir, reporter, stepIndex, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await TryDownloadVerifyExtractAsync(url, assetName, expectedSha256, expectedSize, variantDir, reporter, stepIndex, ct).ConfigureAwait(false);
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
    private async Task<Exception?> TryDownloadVerifyExtractAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string variantDir, AcquisitionReporter? reporter, int stepIndex,
        CancellationToken ct)
    {
        // Defense-in-depth: even though assetName is allow-list-validated upstream, strip any directory component before
        // it composes a temp path so a future caller can never traverse out of the temp dir.
        var tempArchive = Path.Combine(Path.GetTempPath(), $"llamacpp-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        try
        {
            // Announced before the request so the UI narrates the wait for the response headers too, not just the body.
            reporter?.Report(RuntimeAcquisitionPhase.Downloading, stepIndex);
            await DownloadToFileAsync(url, tempArchive, expectedSize, reporter, stepIndex, ct).ConfigureAwait(false);

            // When the catalog reported a size, the on-disk length must match it exactly before we trust+hash the file.
            if (expectedSize > 0 && new FileInfo(tempArchive).Length != expectedSize)
            {
                return new LlamaRuntimeException("The llama.cpp runtime download did not match its expected size.");
            }

            // Verification and extraction of a few-hundred-MB archive are not instant; without their own phases the UI
            // would sit at 100 % of the byte counter with nothing to explain the remaining wait.
            reporter?.Report(RuntimeAcquisitionPhase.Verifying, stepIndex);
            if (!await HashMatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return new LlamaRuntimeException("The llama.cpp runtime download failed integrity verification.");
            }

            reporter?.Report(RuntimeAcquisitionPhase.Extracting, stepIndex);
            await ExtractArchiveAsync(tempArchive, assetName, variantDir, ct).ConfigureAwait(false);
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

    private async Task DownloadToFileAsync(Uri url, string destination, long expectedSize, AcquisitionReporter? reporter, int stepIndex, CancellationToken ct)
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

        // The determinate total comes from the response headers; the pinned path has no catalog-reported size, so it is
        // simply absent (null) until — and unless — a Content-Length lands, and the UI stays indeterminate meanwhile.
        var totalBytes = response.Content.Headers.ContentLength;

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

                // Reported unconditionally per ~80 KB chunk: the registry owns the push throttle, and duplicating it here
                // would only make the hydrate snapshot lag the bytes actually on disk.
                reporter?.Report(RuntimeAcquisitionPhase.Downloading, stepIndex, written, totalBytes);
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

    private static async Task ExtractArchiveAsync(string archivePath, string assetName, string variantDir, CancellationToken ct)
    {
        // Extract into a temp sibling then atomically move into place so a partial extract can't masquerade as cached.
        var stagingDir = $"{variantDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await ZipFile.ExtractToDirectoryAsync(archivePath, stagingDir, ct).ConfigureAwait(false);
            }
            else if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractTarGzAsync(archivePath, stagingDir, ct).ConfigureAwait(false);
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

    private static async Task ExtractTarGzAsync(string archivePath, string destination, CancellationToken ct)
    {
        await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, ct).ConfigureAwait(false);
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

    /// <summary>
    ///     The directory every acquired llama.cpp runtime is cached under for the default app-data root
    ///     (<c>{cacheRoot}/llama.cpp</c>, the layout <see cref="EnsureBinaryAsync" /> writes its variant dirs into).
    /// </summary>
    /// <remarks>
    ///     Exposed so the startup orphan reaper matches ONLY <c>llama-server</c> binaries this app acquired, never an
    ///     unrelated install.
    /// </remarks>
    internal static string DefaultLlamaCppBinariesRoot()
    {
        return Path.Combine(RuntimeCacheDirectory.Resolve(), "llama.cpp");
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
