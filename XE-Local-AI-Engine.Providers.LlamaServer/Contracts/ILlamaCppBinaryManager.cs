namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Resolves, downloads, hash-verifies, and caches a prebuilt llama.cpp binary for the host. Never source-builds.
/// </summary>
/// <remarks>
///     Responsibility: pick the prebuilt release asset for the OS/arch + requested <see cref="GpuVariant" />
///     → download via <see cref="HttpClient" /> → verify SHA256 against the pinned hash (corrupt → re-download once then
///     surface a sanitized error) → cache under a stable app dir → track the recommended-pinned version vs a
///     user-selected upgrade (an upgrade must never delete the pinned fallback) → offline uses the cached pinned binary.
/// </remarks>
public interface ILlamaCppBinaryManager
{
    /// <summary>
    ///     Ensures a hash-verified <c>llama-server</c> binary for <paramref name="variant" /> is present on disk and
    ///     returns its resolved location. Idempotent: a cached, hash-valid binary is reused without re-download.
    /// </summary>
    /// <exception cref="LlamaRuntimeException">
    ///     The binary could not be acquired (download failure, repeated hash mismatch, no prebuilt for the host) — the
    ///     message is sanitized for direct display.
    /// </exception>
    Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Resolves an ALREADY-AVAILABLE <c>llama-server</c> for <paramref name="variant" /> and returns
    ///     <see langword="null" /> when none is, instead of acquiring one. Read-only: it never touches the network, never
    ///     creates a directory, and never writes <c>installed-runtime.json</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the surface for diagnostics that only REPORT on the runtime (the device-inventory probe behind the
    ///         hardware-profile page). A read-only GET must not have a multi-hundred-megabyte side effect, on any external
    ///         access profile, so those callers ask this instead of
    ///         <see cref="EnsureBinaryAsync(GpuVariant,CancellationToken)" /> and degrade to "unknown" when the answer is
    ///         null.
    ///     </para>
    ///     <para>
    ///         <b>Read-only means, precisely:</b> no network request of any kind; no directory is created (the cache tree
    ///         is only probed with <see cref="File.Exists(string)" /> / <see cref="Directory.Exists(string)" />); and
    ///         <c>installed-runtime.json</c> is READ but never written — not to record a resolved runtime, and not to
    ///         self-heal a stale source-build record (that discard is left to the next ensure, so a stale record simply
    ///         reads here as "not resolvable").
    ///     </para>
    ///     <para>
    ///         <b>It is not process-free.</b> When an operator bring-your-own override is configured, the override is
    ///         validated exactly as an ensure validates it, which spawns the binary twice under bounded, tree-killed
    ///         timeouts: a <c>--version</c> smoke test and, for a GPU variant, a <c>--list-devices</c> GPU-presence check.
    ///         A configured-but-broken override still THROWS rather than reporting "nothing installed" — that refusal is
    ///         the operator-actionable answer, not a glitch, and the probe surfaces it as the undetermined-backend reason.
    ///     </para>
    ///     <para>
    ///         <b>It deliberately skips the live-catalog tier</b> of the 3-tier resolve (that tier is a network call, and
    ///         it can only choose a tag to ACQUIRE — it cannot make a binary appear on disk). So this reports what is
    ///         installed NOW, which may lag the tag a later explicit
    ///         <see cref="EnsureBinaryAsync(GpuVariant,CancellationToken)" /> would resolve and acquire.
    ///     </para>
    /// </remarks>
    /// <exception cref="LlamaRuntimeException">A configured bring-your-own override failed validation.</exception>
    Task<LlamaBinary?> TryGetInstalledBinaryAsync(GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Runtime endpoint ensure surface. The caller owns <paramref name="mutationLease" /> through completion; the
    ///     supervisor spawn path uses the non-lease overload because its registered inflight spawn is the exclusion token.
    /// </summary>
    Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant,
        ILlamaServerRuntimeMutationLease mutationLease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutationLease);
        return EnsureBinaryAsync(variant, ct);
    }

    /// <summary>
    ///     Installs a specific, dynamically-resolved release: downloads <paramref name="assetName" /> for
    ///     <paramref name="tag" />, verifies it against the live publisher <paramref name="digestSha256" /> (a
    ///     <c>sha256:</c> prefix is tolerated), atomically extracts into the versioned cache dir, smoke-tests the resolved
    ///     <c>llama-server</c>, and — only on success — records the install in <c>installed-runtime.json</c>. On any
    ///     failure the previously-installed binary is left untouched and a sanitized error is surfaced.
    /// </summary>
    /// <param name="expectedSize">
    ///     The catalog-reported asset size in bytes. A non-positive value means "unknown" and only the absolute download
    ///     ceiling is enforced; a positive value adds a pre-download ceiling check and a post-download length match.
    /// </param>
    /// <exception cref="LlamaRuntimeException">
    ///     The tag/asset name is malformed, or the download / size / digest verification / smoke test failed — sanitized
    ///     for display.
    /// </exception>
    /// <remarks>
    ///     Direct callers are limited to startup provisioning before the process supervisor accepts work. Runtime/API
    ///     callers must use the lease-bearing overload below. Both paths fail closed while a source-runtime record exists.
    /// </remarks>
    Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Runtime endpoint mutation surface. The caller owns <paramref name="mutationLease" /> and must hold it through
    ///     completion; the manager validates source-record exclusion but neither acquires nor disposes the supervisor lease.
    ///     This explicit ownership avoids recursive lease acquisition while preventing endpoint updates during starts.
    /// </summary>
    Task<LlamaBinary> InstallTagAsync(string tag,
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

    /// <summary>
    ///     Adopts an in-app source-built CUDA runtime: validates the freshly-built <c>llama-server</c> under
    ///     <paramref name="buildBinDir" /> (full path-chain perms/ownership + <c>--version</c> smoke + <c>--list-devices</c>
    ///     GPU presence), records it in <c>installed-runtime.json</c> with its computed SHA256 and
    ///     <see cref="InstalledRuntimeState.SourceBuildPath" /> set, and marks the managed-CUDA cached signal available. A
    ///     validation failure throws a sanitized <see cref="LlamaRuntimeException" /> and records nothing — a failed build
    ///     never becomes active and never silently degrades to CPU.
    /// </summary>
    /// <param name="buildBinDir">Absolute directory holding the built <c>llama-server</c> (and its sibling <c>.so</c> files).</param>
    /// <param name="tag">The pinned llama.cpp tag the build was produced from (for the runtime record + rebuild-staleness).</param>
    /// <exception cref="LlamaRuntimeException">Validation failed (path-chain, smoke, or GPU presence) — sanitized for display.</exception>
    Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct);

    /// <summary>Adopts a generalized source-built runtime with exact provenance.</summary>
    Task<InstalledRuntimeState> AdoptSourceBuildAsync(string buildBinDir,
        string tag,
        GpuVariant variant,
        string sourceRepository,
        string sourceCommit,
        LlamaCppSourceRevisionMode revisionMode,
        string? requestedCommit,
        CancellationToken ct)
    {
        throw new NotSupportedException("Generalized source-build adoption is not supported by this binary manager.");
    }

    /// <summary>Adopts a generalized source build while preserving the explicit source-selection intent.</summary>
    Task<InstalledRuntimeState> AdoptSourceBuildAsync(string buildBinDir,
        string tag,
        GpuVariant variant,
        string sourceRepository,
        string sourceCommit,
        LlamaCppSourceRevisionMode revisionMode,
        string? requestedCommit,
        LlamaCppSourceSelection sourceSelection,
        CancellationToken ct)
    {
        return AdoptSourceBuildAsync(buildBinDir, tag, variant, sourceRepository, sourceCommit, revisionMode, requestedCommit, ct);
    }

    /// <summary>
    ///     Removes the recorded managed source-built runtime: deletes the on-disk build tree ONLY when the recorded
    ///     <see cref="InstalledRuntimeState.SourceBuildPath" /> is byte-for-byte the bin directory of a location this
    ///     manager computes itself under the cache root — either the generalized <c>source-build/active</c> tree or the
    ///     pre-generalization <c>source-cuda/{PinnedTag}</c> one. Any other recorded path is NEVER deleted; the record
    ///     and the cached signal are cleared regardless, so no caller reports a successful removal while stale source
    ///     state stays active. Idempotent: a no-op when no source build is recorded. <c>[secMED-3]</c>
    /// </summary>
    Task RemoveSourceBuildAsync(CancellationToken ct);
}
