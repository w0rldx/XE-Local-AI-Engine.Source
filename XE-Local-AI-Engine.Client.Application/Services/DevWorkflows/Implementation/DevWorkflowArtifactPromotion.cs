namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Copies what an agent node run's work session produced into the run's own artifact record.
///     <para>
///         An application-layer composition rather than a store method, because it spans three things no store can
///         reach at once: the work session's bytes, the run's blob store, and the run's artifact rows. The session is
///         execution scratch and can be deleted with its node run; the run's artifacts are the audit and outlive it, so
///         the bytes are copied rather than referenced.
///     </para>
///     <para>
///         Idempotent by construction: both the artifact id and the append's operation id are derived from
///         <c>(run, node key, attempt, artifact name)</c>, so a promotion replayed after a crash rewrites the same blob
///         and the store's query-first check returns the recorded result instead of appending a second version.
///     </para>
/// </summary>
internal sealed class DevWorkflowArtifactPromotion
{
    private readonly ILogger<DevWorkflowArtifactPromotion> _logger;
    private readonly IDevWorkflowArtifactBlobStore _runBlobs;
    private readonly IWorkSessionArtifactBlobStore _sessionBlobs;
    private readonly IAgentWorkSessionStore _sessions;
    private readonly IDevWorkflowStore _store;

    public DevWorkflowArtifactPromotion(IDevWorkflowStore store,
        IAgentWorkSessionStore sessions,
        IWorkSessionArtifactBlobStore sessionBlobs,
        IDevWorkflowArtifactBlobStore runBlobs,
        ILogger<DevWorkflowArtifactPromotion> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionBlobs = sessionBlobs ?? throw new ArgumentNullException(nameof(sessionBlobs));
        _runBlobs = runBlobs ?? throw new ArgumentNullException(nameof(runBlobs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Promotes every readable artifact of the session and answers how many landed on the run.
    ///     <para>
    ///         <paramref name="declaredKind" /> is what the NODE says it produces, which is the only place that fact can
    ///         come from: the work session's own four kinds have no word for a task package or a plan, so a node that
    ///         another node reads a specific kind from has to declare it. See <see cref="MapKind" />.
    ///     </para>
    /// </summary>
    public async Task<int> PromoteAsync(DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        Guid sessionId,
        DevWorkflowArtifactKind? declaredKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);

        var promoted = 0;
        foreach (var artifact in await _sessions.ListArtifactsAsync(sessionId, sinceSequence: 0, cancellationToken).ConfigureAwait(false))
        {
            if (!artifact.IsValid)
            {
                continue;
            }

            var read = await _sessionBlobs.ReadAsync(sessionId, artifact.Id, artifact.ContentSha256, artifact.SizeBytes, cancellationToken).ConfigureAwait(false);
            if (read.Status != WorkSessionArtifactReadStatus.Found)
            {
                // Skipped rather than failed: the node did its work, and one unreadable artifact must not throw away
                // the rest of it. The session's own row still says the bytes are gone.
                _logger.LogWarning("Work session artifact {ArtifactId} could not be promoted to development workflow run {RunId}: {Status}.",
                    artifact.Id,
                    run.Id,
                    read.Status);
                continue;
            }

            var artifactId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, $"artifact:{artifact.Name}");
            var write = await _runBlobs.WriteAsync(run.Id, artifactId, read.Content, cancellationToken).ConfigureAwait(false);
            var result = await _store.AppendArtifactAsync(new AppendDevWorkflowArtifactCommand(run.Id,
                                             artifactId,
                                             nodeRun.Id,
                                             DevWorkflowVersions.Any,
                                             DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, $"promote:{artifact.Name}"),
                                             MapKind(artifact.Kind, declaredKind),
                                             artifact.Name,
                                             artifact.MediaType,
                                             write.ContentHash,
                                             write.ByteCount,
                                             write.OpaqueReference),
                                         cancellationToken)
                                     .ConfigureAwait(false);
            promoted++;

            if (result.SupersededArtifactId is not { } superseded)
            {
                continue;
            }

            // Mark-only propagation: a node run that consumed the version this one just replaced is flagged, and a
            // human decides what to do about it. Nothing is regenerated, and the superseded bytes stay — versioning is
            // the point.
            _ = await _store.MarkDependentsStaleAsync(new MarkDevWorkflowStaleCommand(run.Id,
                                    superseded,
                                    artifactId,
                                    DevWorkflowVersions.Any,
                                    DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, $"stale:{artifact.Name}")),
                                cancellationToken)
                            .ConfigureAwait(false);
        }

        return promoted;
    }

    /// <summary>
    ///     The session's four artifact kinds onto the run's ten.
    ///     <para>
    ///         <c>Patch</c> maps exactly. <c>Report</c> is the session's word for "the structured result of this work",
    ///         so it — and only it — takes the node's DECLARED kind when the node declares one: that is how the richer
    ///         kinds (<c>TaskPackage</c>, <c>Plan</c>, <c>Specification</c>) become reachable at all, since the work
    ///         session enum has no member for any of them and inferring one from the bytes would be guessing. Note and
    ///         File are the session's scratch, and a node's declared output is not what they are.
    ///     </para>
    /// </summary>
    private static DevWorkflowArtifactKind MapKind(AgentWorkSessionArtifactKind kind, DevWorkflowArtifactKind? declaredKind) =>
        kind switch
        {
            AgentWorkSessionArtifactKind.Patch => DevWorkflowArtifactKind.Patch,
            AgentWorkSessionArtifactKind.Report => declaredKind ?? DevWorkflowArtifactKind.Report,
            _ => DevWorkflowArtifactKind.Report
        };
}
