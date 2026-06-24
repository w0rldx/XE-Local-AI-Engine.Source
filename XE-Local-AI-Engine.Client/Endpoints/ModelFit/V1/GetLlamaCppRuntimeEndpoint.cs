namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Read-only dynamic-runtime status (GET model-fit/llamacpp/runtime): the installed runtime (when recorded), the
///     recommended tag, the optional upstream-latest tag, and whether a newer recommended runtime is available. It reads
///     the shared <see cref="ILlamaCppUpdateState" /> snapshot (computed once at startup) plus the
///     <see cref="IInstalledRuntimeStore" /> record — it MUST NOT trigger a binary download.
///     <para>
///         A <c>?refresh=true</c> query forces a fresh catalog tag-resolution (recommended + upstream-latest) and
///         re-records the snapshot. This still resolves only tags — never an asset — so it never downloads a binary. The
///         refresh is offline-tolerant: an unreachable catalog yields an <c>isOffline</c> status, not an error.
///     </para>
/// </summary>
public sealed class GetLlamaCppRuntimeEndpoint(
    ILlamaCppUpdateState updateState,
    IInstalledRuntimeStore installedRuntimeStore,
    ILlamaCppReleaseCatalog releaseCatalog,
    INodeRuntimeSettings nodeRuntimeSettings,
    ILlamaServerProcessSupervisor processSupervisor) : Endpoint<GetLlamaCppRuntimeRequest, LlamaCppRuntimeStatusResponse>
{
    // Minimum spacing between live catalog refreshes. The unauthenticated GitHub API is 60/hr per IP; a refresh hits it
    // twice (recommended + upstream-latest), so a snapshot younger than this is served from cache even on ?refresh=true.
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IInstalledRuntimeStore _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
    private readonly INodeRuntimeSettings _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
    private readonly ILlamaServerProcessSupervisor _processSupervisor = processSupervisor ?? throw new ArgumentNullException(nameof(processSupervisor));
    private readonly ILlamaCppReleaseCatalog _releaseCatalog = releaseCatalog ?? throw new ArgumentNullException(nameof(releaseCatalog));
    private readonly ILlamaCppUpdateState _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.LlamaCppRuntime);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetLlamaCppRuntimeRequest req, CancellationToken ct)
    {
        var refresh = req.Refresh ?? false;

        var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(ct).ConfigureAwait(false);
        var installed = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);

        var current = _updateState.Current;

        // Rate-limit guard: honor ?refresh=true only when the cached snapshot is older than the minimum interval, so a
        // rapid refresh loop can't burn the 60/hr GitHub budget. A fresh-enough snapshot is reused as-is.
        var allowRefresh = refresh && IsStale(current.CheckedAtUtc);

        var snapshot = allowRefresh
            ? await ComputeFreshSnapshotAsync(recommendedTag, installed?.Tag, ct).ConfigureAwait(false)
            : current;

        // Source the running-process count from the supervisor (the source of truth for llama.cpp binaries — Ollama is
        // an opt-in external provider and is never represented here). This is the cheap in-memory count, NOT a per-process
        // health probe: this GET is a hot path (page mount + the global update banner poll), so no live HTTP probe runs.
        var runningProcessCount = _processSupervisor.CountRunningProcesses();

        await Send.OkAsync(snapshot.ToRuntimeStatusResponse(installed, recommendedTag, runningProcessCount), ct).ConfigureAwait(false);
    }

    // A snapshot that was never computed (null) or is older than the minimum interval may be refreshed against the live
    // catalog. "Now" uses DateTimeOffset.UtcNow to stay consistent with how this endpoint stamps CheckedAtUtc.
    private static bool IsStale(DateTimeOffset? checkedAtUtc)
    {
        return checkedAtUtc is not { } checkedAt || DateTimeOffset.UtcNow - checkedAt >= MinRefreshInterval;
    }

    // Resolves recommended + upstream-latest tags against the live catalog (tags only — never an asset, so no download)
    // and records the recomputed snapshot. Offline/rate-limited resolution degrades to an isOffline snapshot.
    private async Task<LlamaCppUpdateSnapshot> ComputeFreshSnapshotAsync(string recommendedTag, string? installedTag, CancellationToken ct)
    {
        var recommendedResult = await _releaseCatalog.ResolveRecommendedAsync(recommendedTag, ct).ConfigureAwait(false);
        var upstreamResult = await _releaseCatalog.ResolveUpstreamLatestAsync(ct).ConfigureAwait(false);

        var resolvedRecommended = recommendedResult.Tag;
        var isOffline = recommendedResult.IsOffline || recommendedResult.IsRateLimited;

        var updateAvailable = resolvedRecommended is not null
                              && !string.Equals(installedTag, resolvedRecommended, StringComparison.Ordinal);

        var snapshot = new LlamaCppUpdateSnapshot(installedTag,
            RecommendedTag: resolvedRecommended ?? recommendedTag,
            UpstreamLatestTag: upstreamResult.Tag,
            updateAvailable,
            isOffline,
            CheckedAtUtc: DateTimeOffset.UtcNow);

        _updateState.Store(snapshot);
        return snapshot;
    }
}
