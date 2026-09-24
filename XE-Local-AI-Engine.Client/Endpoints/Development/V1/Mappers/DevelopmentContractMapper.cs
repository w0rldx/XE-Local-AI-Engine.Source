namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

internal static class DevelopmentContractMapper
{
    /// <summary>
    ///     Projects the profile as its id, build target and digest rather than the stored blob.
    /// </summary>
    /// <remarks>
    ///     The build target is repository-relative by construction, so nothing host-identifying crosses the boundary;
    ///     the full profile's argument vectors would, and buy the operator nothing.
    /// </remarks>
    public static DevelopmentProjectResponse ToResponse(this DevelopmentProjectSnapshot value)
    {
        var profile = DevelopmentProfileSummary.TryFrom(value.CommandProfileJson);
        return new DevelopmentProjectResponse
        {
            Id = value.Id,
            Objective = value.Objective,
            SelectedFolderId = value.SelectedFolderId,
            RepositoryConnectionRequired = value.SelectedFolderId is null,
            BaseBranch = value.BaseBranch,
            Status = value.Status.ToString(),
            EgressPolicy = value.EgressPolicy.ToString(),
            CoderModelId = value.CoderModelId,
            ReviewerModelId = value.ReviewerModelId,
            MaxTokens = value.MaxTokens,
            MaxDurationSeconds = value.MaxDurationSeconds,
            CreatedAtUtc = value.CreatedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc,
            Version = value.Version,
            CommandProfileId = profile?.ProfileId,
            CommandProfileBuildTarget = profile?.BuildTarget,
            CommandProfileDigest = profile?.Digest
        };
    }

    public static DevelopmentRepositoryResponse ToResponse(this DevelopmentRepositoryReference value) =>
        new()
        {
            Id = value.Id,
            Alias = value.Alias,
            Availability = value.Availability
        };

    public static DevelopmentTemplateResponse ToResponse(this DevelopmentTemplateReference value) =>
        new()
        {
            Id = value.Id,
            Alias = value.Alias,
            Availability = value.Availability
        };

    /// <summary>
    ///     The task's own row says nothing about workflows, so the run driving it travels beside it — from the
    ///     aggregate, which is where the reverse lookup happens.
    /// </summary>
    public static DevelopmentTaskResponse ToResponse(this DevelopmentTaskSnapshot value, Guid? workflowRunId = null) =>
        new()
        {
            Id = value.Id,
            ProjectId = value.ProjectId,
            Title = value.Title,
            Requirements = value.Requirements,
            AcceptanceCriteriaJson = value.AcceptanceCriteriaJson,
            Status = value.Status.ToString(),
            CurrentReviewRound = value.CurrentReviewRound,
            MaxReviewRounds = value.MaxReviewRounds,
            BlockedReason = value.BlockedReason,
            ApprovedSubjectHash = value.ApprovedSubjectHash,
            Version = value.Version,
            WorkflowRunId = workflowRunId
        };

    public static DevelopmentAttemptResponse ToResponse(this DevelopmentAttemptSnapshot value) =>
        new()
        {
            Id = value.Id,
            TaskId = value.TaskId,
            PredecessorAttemptId = value.PredecessorAttemptId,
            Role = value.Role.ToString(),
            ModelId = value.ModelId,
            Provider = value.Provider,
            Status = value.Status.ToString(),
            StartedAtUtc = value.StartedAtUtc,
            EndedAtUtc = value.EndedAtUtc,
            TerminalReason = value.TerminalReason,
            InputTokens = value.InputTokens,
            OutputTokens = value.OutputTokens,
            Version = value.Version
        };

    public static DevelopmentArtifactResponse ToResponse(this DevelopmentArtifactSnapshot value) =>
        new()
        {
            Id = value.Id,
            ProjectId = value.ProjectId,
            TaskId = value.TaskId,
            AttemptId = value.AttemptId,
            Kind = value.Kind.ToString(),
            ContentHash = value.ContentHash,
            ByteCount = value.ByteCount,
            CreatedAtUtc = value.CreatedAtUtc,
            BaseCommit = value.BaseCommit,
            SubjectHash = value.SubjectHash,
            ChangedFilesManifestHash = value.ChangedFilesManifestHash,
            CommandProfileVersion = value.CommandProfileVersion,
            CommandProfileDigest = value.CommandProfileDigest,
            IsValid = value.IsValid
        };

    public static DevelopmentEventResponse ToResponse(this DevelopmentEventSnapshot value) =>
        new()
        {
            Id = value.Id,
            ProjectId = value.ProjectId,
            TaskId = value.TaskId,
            AttemptId = value.AttemptId,
            Sequence = value.Sequence,
            EventType = value.EventType,
            OccurredAtUtc = value.OccurredAtUtc,
            OperationId = value.OperationId,
            OperationPhase = value.OperationPhase,
            Outcome = value.Outcome,
            Reason = value.Reason
        };

    public static DevelopmentTaskDetailResponse ToResponse(this DevelopmentTaskAggregate value) =>
        new()
        {
            Task = value.Task.ToResponse(value.WorkflowRunId),
            Attempts = value.Attempts.Select(ToResponse).ToArray(),
            Artifacts = value.Artifacts.Select(ToResponse).ToArray()
        };

    public static DevelopmentProjectDetailResponse ToResponse(this DevelopmentProjectAggregate value) =>
        new()
        {
            Project = value.Project.ToResponse(),
            Tasks = value.Tasks.Select(ToResponse).ToArray(),
            Events = value.Events.Select(ToResponse).ToArray()
        };

    /// <summary>
    ///     Projects one sandbox role's SERVED isolation posture — the role's own declaration INTERSECTED with what its
    ///     provider advertises, never a capability read-out — into the operator-facing summary.
    /// </summary>
    /// <remarks>
    ///     Network is the one axis NOT intersected with the role's <c>NetworkFloor</c>: the floor is the weakest posture a workload will ACCEPT, while every consumer
    ///     requests <c>SandboxNetworkPolicy.None</c> wherever the flag is advertised (<c>AgentHomeService.ResolveNetworkPolicy</c>,
    ///     <c>DevelopmentWorkspaceProvider.ResolveAgentFacingNetworkPolicy</c>, <c>ComputeToolGateway.BuildCreateRequest</c>), so the flag IS the served posture.
    ///     <see cref="SandboxIsolationSummaryResponse.Level" /> counts the three served axes — three, one-or-two, zero — with no term for a hardware or VM
    ///     boundary. Rule, flags and the two reasons: docs/wiki/12-security-and-privacy.md ("Backend selection").
    /// </remarks>
    /// <param name="role">The wire role name, as the panel keys its rows on.</param>
    /// <param name="requirements">The role's ADR 0007 declaration from <see cref="SandboxWorkloads" />, passed in so this projection owns no second per-role table.</param>
    /// <param name="containment">The host containment measurement, for the probe reason.</param>
    /// <param name="nodeRequiresEgressDenial">This role's section's <c>RequireEgressDenial</c> switch; defaults to the shipped <see langword="false" />.</param>
    public static SandboxIsolationSummaryResponse ToIsolationSummary(string role,
        SandboxRequirements requirements,
        ISandboxRuntimeProvider provider,
        SandboxContainment containment,
        bool nodeRequiresEgressDenial = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(containment);

        var capabilities = provider.Capabilities;
        var boundaryRequested = requirements.IsolationFloor == SandboxIsolationMode.Filesystem;
        var boundaryAdvertised = capabilities.HasFlag(SandboxProviderCapabilities.SupportsHostFilesystemBoundary);
        var filesystem = boundaryRequested && boundaryAdvertised;
        var network = capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy);
        var networkRequired = SandboxEgressPolicy.IsRequired(requirements, nodeRequiresEgressDenial);
        var limitsAdvertised = capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits);
        var limits = requirements.RequestsResourceLimits && limitsAdvertised;
        var enforced = (filesystem ? 1 : 0) + (network ? 1 : 0) + (limits ? 1 : 0);
        var boundaryReason = boundaryRequested
            ? ToFilesystemIsolationUnavailableReason(provider.ProviderName, containment)
            : ToNotRequestedReason(requirements,
                "filesystem boundary",
                "its commands run in a working-directory jail on the host filesystem and can read whatever the account running the engine can read");
        var limitsReason = requirements.RequestsResourceLimits
            ? ToResourceLimitsUnavailableReason(provider.ProviderName, containment)
            : ToNotRequestedReason(requirements,
                "CPU, memory or process-count ceilings",
                "a runaway command is bounded only by its timeout and the machine");

        return new SandboxIsolationSummaryResponse
        {
            Role = role,
            Provider = provider.ProviderName,
            Backend = ToIsolationBackend(provider.ProviderName, filesystem),
            Level = enforced switch
            {
                3 => "Isolated",
                > 0 => "Confined",
                _ => "None"
            },
            FilesystemIsolation = filesystem,
            NetworkIsolation = network,
            NetworkIsolationRequired = networkRequired,
            ResourceLimits = limits,
            ReadOnlyMounts = capabilities.HasFlag(SandboxProviderCapabilities.SupportsReadOnlyMounts),
            FilesystemIsolationUnavailableReason = filesystem ? null : boundaryReason,
            ResourceLimitsUnavailableReason = limits ? null : limitsReason
        };
    }

    // Derived from the declaration rather than written per role, so a workload added to SandboxWorkloads gets a true
    // sentence without touching this file.
    private static string ToNotRequestedReason(SandboxRequirements requirements, string what, string consequence)
    {
        return $"not requested by this role: '{requirements.Workload}' declares no {what}, so {consequence}";
    }

    // Guarded on the provider for ToFilesystemIsolationUnavailableReason's reason: the containment probe measures the
    // HOST's systemd-run chain, which is the process provider's ceiling mechanism and nobody else's.
    private static string ToResourceLimitsUnavailableReason(string providerName, SandboxContainment containment)
    {
        return string.Equals(providerName, ProcessSandboxRuntimeProvider.Name, StringComparison.Ordinal)
            ? containment.ResourceLimitsUnavailableReason
              ?? "the supervised process sandbox did not advertise resource ceilings on this host"
            : $"the '{providerName}' sandbox provider does not advertise resource ceilings";
    }

    private static string ToIsolationBackend(string providerName, bool filesystemIsolation)
    {
        return providerName switch
        {
            ProcessSandboxRuntimeProvider.Name => filesystemIsolation ? "bwrap" : "process",
            DockerSandboxRuntimeProvider.Name => "docker",
            _ => "none"
        };
    }

    // The containment probe measures the HOST bubblewrap chain, which is the process provider's boundary and nobody else's. Attributing its reason to another
    // provider would tell an operator that a container role is unisolated because this host lacks bwrap, which is not why.
    private static string ToFilesystemIsolationUnavailableReason(string providerName, SandboxContainment containment)
    {
        if (string.Equals(providerName, ProcessSandboxRuntimeProvider.Name, StringComparison.Ordinal))
        {
            return containment.FilesystemIsolationUnavailableReason
                   ?? "the supervised process sandbox did not advertise a filesystem boundary on this host";
        }

        // The container provider no longer reaches here: it advertises the boundary, so this projection reports no
        // reason for it. The generic arm stays for a backend added later that advertises neither.
        return string.Equals(providerName, FakeSandboxRuntimeProvider.Name, StringComparison.Ordinal)
            ? "the deterministic in-memory provider has no mount namespace and never will"
            : $"the '{providerName}' sandbox provider does not advertise a filesystem boundary";
    }
}

/// <summary>Projects the container-runtime preflight onto its wire contract.</summary>
internal static class DevelopmentContainerRuntimeMapper
{
    public static DevelopmentContainerRuntimeResponse ToResponse(this DockerDaemonPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        return new DevelopmentContainerRuntimeResponse
        {
            Ready = preflight.Ready,
            Status = ToStatusCode(preflight.Status),
            Message = preflight.Message,
            RequiresOperatorConfirmation = preflight.RequiresOperatorConfirmation,
            Endpoint = preflight.Endpoint?.Display,
            EndpointSource = preflight.Endpoint is null ? null : ToSourceCode(preflight.Endpoint.Source),
            ObservedDaemon = preflight.ObservedDaemon is null
                ? null
                : new DevelopmentContainerDaemonResponse
                {
                    DaemonId = preflight.ObservedDaemon.DaemonId,
                    ServerVersion = preflight.ObservedDaemon.ServerVersion,
                    Endpoint = preflight.ObservedDaemon.Endpoint.Display,
                    ConfirmedAtUtc = null
                },
            PinnedDaemon = preflight.PinnedDaemon is null
                ? null
                : new DevelopmentContainerDaemonResponse
                {
                    DaemonId = preflight.PinnedDaemon.DaemonId,
                    ServerVersion = preflight.PinnedDaemon.ServerVersion,
                    Endpoint = preflight.PinnedDaemon.Endpoint,
                    ConfirmedAtUtc = preflight.PinnedDaemon.ConfirmedAtUtc
                }
        };
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
