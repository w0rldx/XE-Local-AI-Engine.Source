namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class CreateDevelopmentProjectRequest
{
    public Guid OperationId { get; init; }
    public Guid SelectedFolderId { get; init; }
    public string Objective { get; init; } = string.Empty;
    public string BaseBranch { get; init; } = "main";
    public string TaskTitle { get; init; } = string.Empty;
    public string Requirements { get; init; } = string.Empty;
    public string AcceptanceCriteriaJson { get; init; } = "[]";
    public string EgressPolicy { get; init; } = nameof(DevelopmentEgressPolicy.LocalOnly);
    public string CoderModelId { get; init; } = string.Empty;
    public string ReviewerModelId { get; init; } = string.Empty;
    public bool TrustedRepositoryAcknowledged { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxDurationSeconds { get; init; }

    /// <summary>
    ///     The profile the operator confirmed at the detection step. Null means "run detection", which is what the
    ///     confirmation step proposed in the first place.
    /// </summary>
    public string? CommandProfileId { get; init; }

    /// <summary>The repository-relative solution or project file the confirmed profile builds. Null for generic-git.</summary>
    public string? BuildTarget { get; init; }
}

public sealed class DevelopmentProjectRequest
{
    public Guid ProjectId { get; init; }
}

public sealed class DevelopmentTaskRequest
{
    public Guid ProjectId { get; init; }
    public Guid TaskId { get; init; }
}

public sealed class DevelopmentAttemptRequest
{
    public Guid ProjectId { get; init; }
    public Guid TaskId { get; init; }
    public Guid AttemptId { get; init; }
}

public sealed class DevelopmentArtifactRequest
{
    public Guid ProjectId { get; init; }
    public Guid TaskId { get; init; }
    public Guid ArtifactId { get; init; }
}

public sealed class DevelopmentActionRequest
{
    public Guid ProjectId { get; init; }
    public Guid TaskId { get; init; }
    public Guid OperationId { get; init; }
}

public sealed class RegisterDevelopmentRepositoryRequest
{
    public string Alias { get; init; } = string.Empty;
    public string HostPath { get; init; } = string.Empty;
}

public sealed class RegisterDevelopmentTemplateRequest
{
    /// <summary>The label the operator will pick this template by.</summary>
    public string Alias { get; init; } = string.Empty;

    /// <summary>Absolute host path of an existing Git repository to use as a template.</summary>
    public string HostPath { get; init; } = string.Empty;
}

public sealed class DevelopmentTemplateRequest
{
    public Guid TemplateId { get; init; }
}

/// <summary>
///     Creates a new repository from a template and registers it. The engine clones the template, drops its
///     <c>.git</c>, re-initializes, and makes one initial commit — so the result is a standalone repository with no
///     remote and none of the template's history.
/// </summary>
public sealed class CreateDevelopmentRepositoryFromTemplateRequest
{
    public Guid TemplateId { get; init; }

    /// <summary>Absolute destination path chosen by the operator. Must not live under the node data directory.</summary>
    public string DestinationPath { get; init; } = string.Empty;

    /// <summary>The alias the new repository is registered under.</summary>
    public string Alias { get; init; } = string.Empty;

    /// <summary>
    ///     The branch the initial commit lands on. Must match the branch the project will be created with, because the
    ///     managed worktree resolves its base commit through <c>refs/heads/{branch}</c>.
    /// </summary>
    public string BaseBranch { get; init; } = "main";
}

public sealed class ReconnectDevelopmentRepositoryRequest
{
    public Guid ProjectId { get; init; }
    public Guid SelectedFolderId { get; init; }
    public long ExpectedVersion { get; init; }
}

/// <summary>
///     Whether Development Mode is available, which sandbox provider it executes on, and — only when that provider is
///     the container one — whether the container runtime is usable.
///     <para>
///         Three axes rather than one boolean, deliberately. <see cref="Enabled" /> is this node's own configuration
///         switch; <see cref="SandboxProvider" /> is the provider per-feature selection (D2) actually resolved; and
///         <see cref="ContainerRuntime" /> is the preflight of the machine's container runtime, which ADR 0004 makes a
///         hard requirement for Development Mode execution <em>on that provider</em>. Collapsing them would tell an
///         operator only that Development Mode is unavailable, which is the least useful true statement available: the
///         whole value of the preflight is naming which axis is the problem and what to do about it.
///     </para>
///     <para>
///         <see cref="ContainerRuntime" /> is null when <see cref="SandboxProvider" /> is not the container provider,
///         and that null is the honest answer rather than a missing value: a node running Development Mode on the
///         supervised process sandbox has no container dependency to report, and reporting an unreachable daemon
///         anyway would present a false blocker on a feature that works.
///     </para>
/// </summary>
/// <param name="Enabled">This node's Development Mode configuration switch.</param>
/// <param name="SandboxProvider">The sandbox provider in force for Development Mode (<c>fake</c>, <c>process</c>, or <c>docker</c>).</param>
/// <param name="ContainerRuntime">The container-runtime preflight, present only when the container provider is in force.</param>
/// <param name="Isolation">
///     The isolation posture of every sandbox role on this node, container provider or not. Additive: a consumer that
///     only reads the three axes above is unaffected.
/// </param>
public sealed record DevelopmentCapabilityResponse(bool Enabled,
    string SandboxProvider,
    DevelopmentContainerRuntimeResponse? ContainerRuntime,
    IReadOnlyList<SandboxIsolationSummaryResponse> Isolation);

/// <summary>
///     What one sandbox ROLE is actually isolated by on this host, as the operator sees it.
///     <para>
///         Reported per role rather than once for the node because provider selection is per feature: AgentHome,
///         Development Mode and work sessions each resolve their own provider, and on a mixed node they do not share a
///         posture. Reporting a single number would have to pick one of them and be wrong about the others.
///     </para>
///     <para>
///         Every boolean here is read from the provider's advertised
///         <c>SandboxProviderCapabilities</c> — the same flags the fail-closed launch policy gates on — so this surface
///         cannot claim a boundary the launch path would not enforce. Nothing here describes a hardware or VM boundary:
///         no provider in this tree can prove one, so <see cref="Level" /> deliberately has no term for it.
///     </para>
/// </summary>
/// <param name="Role">The sandbox role: <c>agent-home</c>, <c>development</c>, or <c>work-session</c>.</param>
/// <param name="Provider">The provider resolved for that role (<c>fake</c>, <c>process</c>, or <c>docker</c>).</param>
/// <param name="Backend">The mechanism the boundary is made of: <c>none</c>, <c>process</c>, <c>bwrap</c>, or <c>docker</c>.</param>
/// <param name="Level">The coarse level derived from the three enforcement axes; <c>DevelopmentContractMapper</c> owns the rule.</param>
/// <param name="FilesystemIsolation">Whether a command can run with the host filesystem absent from its mount namespace.</param>
/// <param name="NetworkIsolation">Whether egress can actually be denied.</param>
/// <param name="ResourceLimits">Whether memory / PID / CPU ceilings can actually be imposed.</param>
/// <param name="ReadOnlyMounts">Whether the provider can mount a tree read-only.</param>
/// <param name="FilesystemIsolationUnavailableReason">
///     The measured reason the filesystem boundary is unavailable, or null when it is available. Null is never "we do
///     not know": a role without the boundary always carries a reason.
/// </param>
public sealed record SandboxIsolationSummaryResponse(
    string Role,
    string Provider,
    string Backend,
    string Level,
    bool FilesystemIsolation,
    bool NetworkIsolation,
    bool ResourceLimits,
    bool ReadOnlyMounts,
    string? FilesystemIsolationUnavailableReason);

/// <summary>
///     The container-runtime preflight, as the operator sees it.
///     <para>
///         Per ADR 0004 there is no unisolated fallback: a node without a working container runtime does not get a
///         degraded Development Mode. <see cref="Message" /> is therefore the entire user experience of that failure
///         and always names both the cause and the action; <see cref="Status" /> is the machine-readable code the UI
///         branches on so the prose is never parsed.
///     </para>
/// </summary>
/// <param name="Ready">Whether a Development Mode container could be created right now.</param>
/// <param name="Status">Machine-readable outcome: <c>ready</c>, <c>daemon_unreachable</c>, <c>permission_denied</c>, <c>api_version_too_old</c>, <c>daemon_changed</c>, <c>not_configured</c>, <c>probe_failed</c>.</param>
/// <param name="Message">Operator-facing prose naming the cause and the action.</param>
/// <param name="RequiresOperatorConfirmation">Whether clearing this needs an explicit approval rather than a fix to the machine.</param>
/// <param name="Endpoint">The daemon endpoint the probe used, when one could be resolved.</param>
/// <param name="EndpointSource">How that endpoint was arrived at — the input D10 exists to make visible.</param>
/// <param name="ObservedDaemon">The daemon actually reached, when one answered.</param>
/// <param name="PinnedDaemon">The daemon this node has approved, when it has one.</param>
public sealed record DevelopmentContainerRuntimeResponse(
    bool Ready,
    string Status,
    string Message,
    bool RequiresOperatorConfirmation,
    string? Endpoint,
    string? EndpointSource,
    DevelopmentContainerDaemonResponse? ObservedDaemon,
    DevelopmentContainerDaemonResponse? PinnedDaemon);

/// <summary>
///     One daemon, identified. The installation id is what an operator compares when asked to approve a change, so it
///     crosses the boundary even though it is opaque — without it the confirmation prompt would be asking someone to
///     approve "a different daemon" with nothing to distinguish it by.
/// </summary>
/// <param name="DaemonId">The daemon's own installation id.</param>
/// <param name="ServerVersion">Docker Engine version.</param>
/// <param name="Endpoint">The endpoint this daemon was seen at.</param>
/// <param name="ConfirmedAtUtc">When this node approved it; null for an observed-but-unapproved daemon.</param>
public sealed record DevelopmentContainerDaemonResponse(
    string DaemonId,
    string ServerVersion,
    string Endpoint,
    DateTimeOffset? ConfirmedAtUtc);

/// <summary>
///     Approve the container runtime currently reachable (D10 re-confirmation).
///     <para>
///         <see cref="DaemonId" /> is required and is the daemon the operator was <em>shown</em>. The confirmation is
///         refused if that is not the daemon reachable when the request arrives — otherwise an approval issued against
///         one runtime could land on whichever runtime answered next, and the control would be approving something
///         nobody looked at.
///     </para>
/// </summary>
public sealed class ConfirmDevelopmentContainerRuntimeRequest
{
    public string DaemonId { get; init; } = string.Empty;
}

public sealed record DevelopmentRepositoryResponse(string Id, string Alias, string Availability);

/// <summary>
///     A registered template, projected as id plus alias. The host path never crosses this boundary, exactly as it
///     never does for a registered repository.
/// </summary>
public sealed record DevelopmentTemplateResponse(string Id, string Alias, string Availability);

public sealed record ListDevelopmentTemplatesResponse(IReadOnlyList<DevelopmentTemplateResponse> Templates);

/// <summary>
///     The new repository, plus which template and commit produced it. The commit sha is the template's version —
///     templates are living repositories, so a version number would be a lie.
/// </summary>
public sealed record DevelopmentRepositoryFromTemplateResponse(
    DevelopmentRepositoryResponse Repository,
    string TemplateAlias,
    string TemplateCommit);

public sealed record DevelopmentProjectResponse(
    Guid Id,
    string Objective,
    Guid? SelectedFolderId,
    bool RepositoryConnectionRequired,
    string BaseBranch,
    string Status,
    string EgressPolicy,
    string? CoderModelId,
    string? ReviewerModelId,
    int? MaxTokens,
    int? MaxDurationSeconds,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version,
    string? CommandProfileId,
    string? CommandProfileBuildTarget,
    string? CommandProfileDigest);

public sealed record DevelopmentTaskResponse(
    Guid Id,
    Guid ProjectId,
    string Title,
    string Requirements,
    string AcceptanceCriteriaJson,
    string Status,
    int CurrentReviewRound,
    int MaxReviewRounds,
    string? BlockedReason,
    string? ApprovedSubjectHash,
    long Version);

public sealed record DevelopmentAttemptResponse(
    Guid Id,
    Guid TaskId,
    Guid? PredecessorAttemptId,
    string Role,
    string ModelId,
    string Provider,
    string Status,
    long? StartedAtUtc,
    long? EndedAtUtc,
    string? TerminalReason,
    long? InputTokens,
    long? OutputTokens,
    long Version);

public sealed record DevelopmentArtifactResponse(
    Guid Id,
    Guid ProjectId,
    Guid TaskId,
    Guid? AttemptId,
    string Kind,
    string ContentHash,
    long ByteCount,
    long CreatedAtUtc,
    string? BaseCommit,
    string? SubjectHash,
    string? ChangedFilesManifestHash,
    string? CommandProfileVersion,
    string? CommandProfileDigest,
    bool IsValid);

public sealed record DevelopmentEventResponse(
    Guid Id,
    Guid ProjectId,
    Guid? TaskId,
    Guid? AttemptId,
    long Sequence,
    string EventType,
    long OccurredAtUtc,
    Guid? OperationId,
    string? OperationPhase,
    string? Outcome);

public sealed record DevelopmentTaskDetailResponse(
    DevelopmentTaskResponse Task,
    IReadOnlyList<DevelopmentAttemptResponse> Attempts,
    IReadOnlyList<DevelopmentArtifactResponse> Artifacts);

public sealed record DevelopmentProjectDetailResponse(
    DevelopmentProjectResponse Project,
    IReadOnlyList<DevelopmentTaskDetailResponse> Tasks,
    IReadOnlyList<DevelopmentEventResponse> Events);

public sealed record ListDevelopmentProjectsResponse(IReadOnlyList<DevelopmentProjectResponse> Items);

public sealed record ListDevelopmentRepositoriesResponse(IReadOnlyList<DevelopmentRepositoryResponse> Items);

public sealed record ListDevelopmentEventsResponse(IReadOnlyList<DevelopmentEventResponse> Items);

public sealed record ListDevelopmentArtifactsResponse(IReadOnlyList<DevelopmentArtifactResponse> Items);

public sealed record DevelopmentArtifactContentResponse(DevelopmentArtifactResponse Artifact, string Content);

public sealed record DevelopmentNextActionResponse(string Action, Guid ProjectId, Guid TaskId, Guid? AttemptId, string TaskStatus, string? Role);

public sealed record DevelopmentPatchPreviewResponse(
    string SubjectHash,
    string PatchHash,
    string ManifestHash,
    string ExpectedResultHash,
    string Patch,
    IReadOnlyList<DevelopmentPatchPreviewFile> ChangedFiles);

public sealed record DevelopmentApplyResponse(Guid OperationId, string Phase, string Outcome, string Status, long Version, long Sequence);

public sealed class DevelopmentProfileDetectionRequest
{
    public Guid SelectedFolderId { get; init; }
}

/// <summary>
///     A detection proposal for a registered repository. Nothing here is authoritative — the operator confirms or
///     overrides it, and the confirmed choice is what gets snapshotted onto the project.
/// </summary>
public sealed record DevelopmentProfileDetectionResponse(
    string ProfileId,
    string? BuildTarget,
    IReadOnlyList<string> Candidates);
