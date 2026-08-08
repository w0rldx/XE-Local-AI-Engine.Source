namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

internal static class DevelopmentContractMapper
{
    /// <summary>
    ///     Projects the profile as its id, build target and digest rather than the stored blob. The build target is
    ///     repository-relative by construction, so nothing host-identifying crosses the boundary; the full profile's
    ///     argument vectors would, and buy the operator nothing.
    /// </summary>
    public static DevelopmentProjectResponse ToResponse(this DevelopmentProjectSnapshot value)
    {
        var profile = DevelopmentProfileSummary.TryFrom(value.CommandProfileJson);
        return new DevelopmentProjectResponse(value.Id,
            value.Objective,
            value.SelectedFolderId,
            value.SelectedFolderId is null,
            value.BaseBranch,
            value.Status.ToString(),
            value.EgressPolicy.ToString(),
            value.CoderModelId,
            value.ReviewerModelId,
            value.MaxTokens,
            value.MaxDurationSeconds,
            value.CreatedAtUtc,
            value.UpdatedAtUtc,
            value.Version,
            profile?.ProfileId,
            profile?.BuildTarget,
            profile?.Digest);
    }

    public static DevelopmentRepositoryResponse ToResponse(this DevelopmentRepositoryReference value) =>
        new(value.Id, value.Alias, value.Availability);

    public static DevelopmentTemplateResponse ToResponse(this DevelopmentTemplateReference value) =>
        new(value.Id, value.Alias, value.Availability);

    public static DevelopmentTaskResponse ToResponse(this DevelopmentTaskSnapshot value) =>
        new(value.Id,
            value.ProjectId,
            value.Title,
            value.Requirements,
            value.AcceptanceCriteriaJson,
            value.Status.ToString(),
            value.CurrentReviewRound,
            value.MaxReviewRounds,
            value.BlockedReason,
            value.ApprovedSubjectHash,
            value.Version);

    public static DevelopmentAttemptResponse ToResponse(this DevelopmentAttemptSnapshot value) =>
        new(value.Id,
            value.TaskId,
            value.PredecessorAttemptId,
            value.Role.ToString(),
            value.ModelId,
            value.Provider,
            value.Status.ToString(),
            value.StartedAtUtc,
            value.EndedAtUtc,
            value.TerminalReason,
            value.InputTokens,
            value.OutputTokens,
            value.Version);

    public static DevelopmentArtifactResponse ToResponse(this DevelopmentArtifactSnapshot value) =>
        new(value.Id,
            value.ProjectId,
            value.TaskId,
            value.AttemptId,
            value.Kind.ToString(),
            value.ContentHash,
            value.ByteCount,
            value.CreatedAtUtc,
            value.BaseCommit,
            value.SubjectHash,
            value.ChangedFilesManifestHash,
            value.CommandProfileVersion,
            value.CommandProfileDigest,
            value.IsValid);

    public static DevelopmentEventResponse ToResponse(this DevelopmentEventSnapshot value) =>
        new(value.Id,
            value.ProjectId,
            value.TaskId,
            value.AttemptId,
            value.Sequence,
            value.EventType,
            value.OccurredAtUtc,
            value.OperationId,
            value.OperationPhase,
            value.Outcome);

    public static DevelopmentTaskDetailResponse ToResponse(this DevelopmentTaskAggregate value) =>
        new(value.Task.ToResponse(),
            value.Attempts.Select(ToResponse).ToArray(),
            value.Artifacts.Select(ToResponse).ToArray());

    public static DevelopmentProjectDetailResponse ToResponse(this DevelopmentProjectAggregate value) =>
        new(value.Project.ToResponse(),
            value.Tasks.Select(ToResponse).ToArray(),
            value.Events.Select(ToResponse).ToArray());
}

/// <summary>Projects the container-runtime preflight onto its wire contract.</summary>
internal static class DevelopmentContainerRuntimeMapper
{
    public static DevelopmentContainerRuntimeResponse ToResponse(this DockerDaemonPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        return new DevelopmentContainerRuntimeResponse(preflight.Ready,
            ToStatusCode(preflight.Status),
            preflight.Message,
            preflight.RequiresOperatorConfirmation,
            preflight.Endpoint?.Display,
            preflight.Endpoint is null ? null : ToSourceCode(preflight.Endpoint.Source),
            preflight.ObservedDaemon is null
                ? null
                : new DevelopmentContainerDaemonResponse(preflight.ObservedDaemon.DaemonId,
                    preflight.ObservedDaemon.ServerVersion,
                    preflight.ObservedDaemon.Endpoint.Display,
                    ConfirmedAtUtc: null),
            preflight.PinnedDaemon is null
                ? null
                : new DevelopmentContainerDaemonResponse(preflight.PinnedDaemon.DaemonId,
                    preflight.PinnedDaemon.ServerVersion,
                    preflight.PinnedDaemon.Endpoint,
                    preflight.PinnedDaemon.ConfirmedAtUtc));
    }

    // Mapped explicitly rather than by ToString(): these codes are a wire contract the React app branches on, and
    // renaming an enum member should not silently change what a client sees.
    private static string ToStatusCode(DockerDaemonPreflightStatus status)
    {
        return status switch
        {
            DockerDaemonPreflightStatus.Ready => "ready",
            DockerDaemonPreflightStatus.DaemonUnreachable => "daemon_unreachable",
            DockerDaemonPreflightStatus.PermissionDenied => "permission_denied",
            DockerDaemonPreflightStatus.ApiVersionTooOld => "api_version_too_old",
            DockerDaemonPreflightStatus.DaemonIdentityChanged => "daemon_changed",
            DockerDaemonPreflightStatus.NotConfigured => "not_configured",
            _ => "probe_failed"
        };
    }

    private static string ToSourceCode(DockerDaemonEndpointSource source)
    {
        return source switch
        {
            DockerDaemonEndpointSource.Configuration => "configuration",
            DockerDaemonEndpointSource.DockerHostEnvironmentVariable => "docker_host",
            DockerDaemonEndpointSource.DefaultUnixSocket => "default_socket",
            DockerDaemonEndpointSource.UserRuntimeUnixSocket => "user_socket",
            DockerDaemonEndpointSource.WindowsNamedPipe => "windows_pipe",
            _ => "unknown"
        };
    }
}
