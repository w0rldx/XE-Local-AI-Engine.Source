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

/// <summary>Creates a new repository from a template and registers it.</summary>
/// <remarks>
///     The engine clones the template, drops its <c>.git</c>, re-initializes and makes one initial commit, so the
///     result is a standalone repository with no remote and none of the template's history.
/// </remarks>
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
/// </summary>
/// <remarks>
///     Three axes rather than one boolean, deliberately: <see cref="Enabled" /> is this node's configuration switch, <see cref="SandboxProvider" /> the provider
///     per-feature selection resolved, and <see cref="ContainerRuntime" /> the preflight ADR 0004 makes a hard requirement for execution <em>on that provider</em>.
///     Collapsing them would say only that Development Mode is unavailable — the least useful true statement — when the whole value of the preflight is naming
///     which axis is the problem. <see cref="ContainerRuntime" /> is null off the container provider, and that null is honest: such a node has no container
///     dependency, and reporting an unreachable daemon would be a false blocker on a feature that works.
/// </remarks>
public sealed class DevelopmentCapabilityResponse
{
    /// <summary>This node's Development Mode configuration switch.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The sandbox provider in force for Development Mode (<c>fake</c>, <c>process</c>, or <c>docker</c>).</summary>
    public required string SandboxProvider { get; init; }

    /// <summary>The container-runtime preflight, present only when the container provider is in force.</summary>
    public required DevelopmentContainerRuntimeResponse? ContainerRuntime { get; init; }

    /// <summary>
    ///     The isolation posture of every sandbox role on this node, container provider or not. Additive: a consumer that
    ///     only reads the three axes above is unaffected.
    /// </summary>
    public required IReadOnlyList<SandboxIsolationSummaryResponse> Isolation { get; init; }
}

/// <summary>What one sandbox ROLE is actually isolated by on this host, as the operator sees it.</summary>
/// <remarks>
///     Per role, not per node: provider selection is per feature, so a mixed node has no single posture. Every boolean is the SERVED posture — the role's own ADR 0007
///     declaration in <c>SandboxWorkloads</c> INTERSECTED with the provider's advertised <c>SandboxProviderCapabilities</c>, never a capability read-out; both halves
///     are load-bearing, and <c>DevelopmentContractMapper.ToIsolationSummary</c> owns the rule. Nothing here describes a hardware or VM boundary, so
///     <see cref="Level" /> deliberately has no term for it. See docs/wiki/12-security-and-privacy.md ("Backend selection: a feature declares what it needs, and never
///     names a backend").
/// </remarks>
public sealed record SandboxIsolationSummaryResponse
{
    /// <summary>
    ///     The sandbox role: <c>agent-home</c>, <c>run_python</c>, <c>mcp-stdio</c>, <c>development</c> or
    ///     <c>work-session</c>.
    /// </summary>
    /// <remarks>
    ///     <c>run_python</c> and <c>mcp-stdio</c> resolve the same provider instance as <c>agent-home</c> and are
    ///     still reported separately, because they are the roles that declare a filesystem boundary and their served
    ///     posture therefore differs on the same backend. <c>mcp-stdio</c> covers a <c>Sandboxed</c> stdio MCP server
    ///     only; a <c>PrivilegedHost</c> one declares no requirements and has no row.
    /// </remarks>
    public required string Role { get; init; }

    /// <summary>The provider resolved for that role (<c>fake</c>, <c>process</c>, or <c>docker</c>).</summary>
    public required string Provider { get; init; }

    /// <summary>The mechanism the boundary is made of: <c>none</c>, <c>process</c>, <c>bwrap</c>, or <c>docker</c>.</summary>
    public required string Backend { get; init; }

    /// <summary>The coarse level derived from the three enforcement axes; <c>DevelopmentContractMapper</c> owns the rule.</summary>
    public required string Level { get; init; }

    /// <summary>
    ///     Whether THIS role's commands run with the host filesystem absent from their mount namespace — the role asks
    ///     for the boundary and the provider serves it.
    /// </summary>
    /// <remarks>
    ///     A provider that could serve one to a role that never asks reports <see langword="false" /> here, with the
    ///     reason saying so.
    /// </remarks>
    public required bool FilesystemIsolation { get; init; }

    /// <summary>
    ///     Whether egress can actually be denied. The capability alone, because every consumer requests
    ///     <c>SandboxNetworkPolicy.None</c> per call exactly where it is advertised, so the flag is the served posture.
    /// </summary>
    public required bool NetworkIsolation { get; init; }

    /// <summary>
    ///     Whether denial is a PRECONDITION for this role on this node rather than a best-effort tightening — the
    ///     difference between "required" and "where available" in the panel.
    /// </summary>
    /// <remarks>
    ///     The operator's whole action on the <c>RequireEgressDenial</c> switches. True when the role's own declaration will not accept egress
    ///     (<c>run_python</c>), or when the node set the switch for the role's section (<c>AgentHome:Sandbox:RequireEgressDenial</c>,
    ///     <c>Development:Sandbox:RequireEgressDenial</c>). Required AND <see cref="NetworkIsolation" /> false is the one combination that means the role will
    ///     REFUSE TO START here: the create site fails closed rather than serving the host's network.
    /// </remarks>
    public required bool NetworkIsolationRequired { get; init; }

    /// <summary>
    ///     Whether memory / PID / CPU ceilings are actually imposed on THIS role — the host can impose them and the
    ///     role asks for them.
    /// </summary>
    /// <remarks>
    ///     <c>SandboxCreateRequest.ResourceLimits</c> is a preference a backend may drop, and a role that passes none
    ///     gets none however capable the host is, so the capability alone is never the served answer.
    /// </remarks>
    public required bool ResourceLimits { get; init; }

    /// <summary>Whether the provider can mount a tree read-only.</summary>
    public required bool ReadOnlyMounts { get; init; }

    /// <summary>Why this role has no filesystem boundary, or null when it has one.</summary>
    /// <remarks>
    ///     Two different sentences, and telling them apart is the operator's whole action: the role does not REQUEST
    ///     one (nothing to fix — it declares an isolation floor of <c>None</c>), or it requests one and the host
    ///     cannot serve it (the measured probe reason — install the missing mechanism, or leave the tool off). Null is
    ///     never "we do not know": a role without the boundary always carries a reason.
    /// </remarks>
    public required string? FilesystemIsolationUnavailableReason { get; init; }

    /// <summary>Why this role has no CPU / memory / process-count ceiling, or null when it has one.</summary>
    /// <remarks>
    ///     The same two sentences as the filesystem reason, and the same operator action behind them: the role does
    ///     not REQUEST ceilings (whether it should is an operator decision, not a bug), or it requests them and the
    ///     host cannot impose them.
    /// </remarks>
    public required string? ResourceLimitsUnavailableReason { get; init; }
}

/// <summary>The container-runtime preflight, as the operator sees it.</summary>
/// <remarks>
///     Per ADR 0004 there is no unisolated fallback: a node without a working container runtime does not get a
///     degraded Development Mode. <see cref="Message" /> is therefore the entire user experience of that failure and
///     always names both the cause and the action; <see cref="Status" /> is the machine-readable code the UI branches
///     on so the prose is never parsed.
/// </remarks>
public sealed class DevelopmentContainerRuntimeResponse
{
    /// <summary>Whether a Development Mode container could be created right now.</summary>
    public required bool Ready { get; init; }

    /// <summary>Machine-readable outcome: <c>ready</c>, <c>daemon_unreachable</c>, <c>permission_denied</c>, <c>api_version_too_old</c>, <c>daemon_changed</c>, <c>not_configured</c>, <c>probe_failed</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Operator-facing prose naming the cause and the action.</summary>
    public required string Message { get; init; }

    /// <summary>Whether clearing this needs an explicit approval rather than a fix to the machine.</summary>
    public required bool RequiresOperatorConfirmation { get; init; }

    /// <summary>The daemon endpoint the probe used, when one could be resolved.</summary>
    public required string? Endpoint { get; init; }

    /// <summary>How that endpoint was resolved, so daemon substitution is visible.</summary>
    public required string? EndpointSource { get; init; }

    /// <summary>The daemon actually reached, when one answered.</summary>
    public required DevelopmentContainerDaemonResponse? ObservedDaemon { get; init; }

    /// <summary>The daemon this node has approved, when it has one.</summary>
    public required DevelopmentContainerDaemonResponse? PinnedDaemon { get; init; }
}

/// <summary>One daemon, identified.</summary>
/// <remarks>
///     The installation id is what an operator compares when asked to approve a change, so it crosses the boundary
///     even though it is opaque — without it the confirmation prompt would be asking someone to approve "a different
///     daemon" with nothing to distinguish it by.
/// </remarks>
public sealed class DevelopmentContainerDaemonResponse
{
    /// <summary>The daemon's own installation id.</summary>
    public required string DaemonId { get; init; }

    /// <summary>Docker Engine version.</summary>
    public required string ServerVersion { get; init; }

    /// <summary>The endpoint this daemon was seen at.</summary>
    public required string Endpoint { get; init; }

    /// <summary>When this node approved it; null for an observed-but-unapproved daemon.</summary>
    public required DateTimeOffset? ConfirmedAtUtc { get; init; }
}

/// <summary>Approve the container runtime currently reachable after re-confirming its identity.</summary>
/// <remarks>
///     <see cref="DaemonId" /> is required and is the daemon the operator was <em>shown</em>. The confirmation is
///     refused if that is not the daemon reachable when the request arrives — otherwise an approval issued against one
///     runtime could land on whichever runtime answered next, and the control would be approving something nobody
///     looked at.
/// </remarks>
public sealed class ConfirmDevelopmentContainerRuntimeRequest
{
    public string DaemonId { get; init; } = string.Empty;
}

public sealed class DevelopmentRepositoryResponse
{
    public required string Id { get; init; }

    public required string Alias { get; init; }

    public required string Availability { get; init; }
}

/// <summary>
///     A registered template, projected as id plus alias. The host path never crosses this boundary, exactly as it
///     never does for a registered repository.
/// </summary>
public sealed class DevelopmentTemplateResponse
{
    public required string Id { get; init; }

    public required string Alias { get; init; }

    public required string Availability { get; init; }
}

public sealed class ListDevelopmentTemplatesResponse
{
    public required IReadOnlyList<DevelopmentTemplateResponse> Templates { get; init; }
}

/// <summary>
///     The new repository, plus which template and commit produced it. The commit sha is the template's version —
///     templates are living repositories, so a version number would be a lie.
/// </summary>
public sealed class DevelopmentRepositoryFromTemplateResponse
{
    public required DevelopmentRepositoryResponse Repository { get; init; }

    public required string TemplateAlias { get; init; }

    public required string TemplateCommit { get; init; }
}

public sealed class DevelopmentProjectResponse
{
    public required Guid Id { get; init; }

    public required string Objective { get; init; }

    public required Guid? SelectedFolderId { get; init; }

    public required bool RepositoryConnectionRequired { get; init; }

    public required string BaseBranch { get; init; }

    public required string Status { get; init; }

    public required string EgressPolicy { get; init; }

    public required string? CoderModelId { get; init; }

    public required string? ReviewerModelId { get; init; }

    public required int? MaxTokens { get; init; }

    public required int? MaxDurationSeconds { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }

    public required string? CommandProfileId { get; init; }

    public required string? CommandProfileBuildTarget { get; init; }

    public required string? CommandProfileDigest { get; init; }
}

/// <summary>
///     <see cref="WorkflowRunId" /> names the development workflow run driving this task, and is absent for the
///     ordinary task an operator drives themselves. A task that has one is approved at that run's gate, so this page
///     defers the apply to it rather than offering its own.
/// </summary>
public sealed class DevelopmentTaskResponse
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required string Title { get; init; }

    public required string Requirements { get; init; }

    public required string AcceptanceCriteriaJson { get; init; }

    public required string Status { get; init; }

    public required int CurrentReviewRound { get; init; }

    public required int MaxReviewRounds { get; init; }

    public required string? BlockedReason { get; init; }

    public required string? ApprovedSubjectHash { get; init; }

    public required long Version { get; init; }

    public required Guid? WorkflowRunId { get; init; }
}

public sealed class DevelopmentAttemptResponse
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid? PredecessorAttemptId { get; init; }

    public required string Role { get; init; }

    public required string ModelId { get; init; }

    public required string Provider { get; init; }

    public required string Status { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? EndedAtUtc { get; init; }

    public required string? TerminalReason { get; init; }

    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }

    public required long Version { get; init; }
}

public sealed class DevelopmentArtifactResponse
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid? AttemptId { get; init; }

    public required string Kind { get; init; }

    public required string ContentHash { get; init; }

    public required long ByteCount { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required string? BaseCommit { get; init; }

    public required string? SubjectHash { get; init; }

    public required string? ChangedFilesManifestHash { get; init; }

    public required string? CommandProfileVersion { get; init; }

    public required string? CommandProfileDigest { get; init; }

    public required bool IsValid { get; init; }
}

public sealed class DevelopmentEventResponse
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid? TaskId { get; init; }

    public required Guid? AttemptId { get; init; }

    public required long Sequence { get; init; }

    public required string EventType { get; init; }

    public required long OccurredAtUtc { get; init; }

    public required Guid? OperationId { get; init; }

    public required string? OperationPhase { get; init; }

    public required string? Outcome { get; init; }

    public string? Reason { get; init; }
}

public sealed class DevelopmentTaskDetailResponse
{
    public required DevelopmentTaskResponse Task { get; init; }

    public required IReadOnlyList<DevelopmentAttemptResponse> Attempts { get; init; }

    public required IReadOnlyList<DevelopmentArtifactResponse> Artifacts { get; init; }
}

public sealed class DevelopmentProjectDetailResponse
{
    public required DevelopmentProjectResponse Project { get; init; }

    public required IReadOnlyList<DevelopmentTaskDetailResponse> Tasks { get; init; }

    public required IReadOnlyList<DevelopmentEventResponse> Events { get; init; }
}

public sealed class ListDevelopmentProjectsResponse
{
    public required IReadOnlyList<DevelopmentProjectResponse> Items { get; init; }
}

public sealed class ListDevelopmentRepositoriesResponse
{
    public required IReadOnlyList<DevelopmentRepositoryResponse> Items { get; init; }
}

public sealed class ListDevelopmentEventsResponse
{
    public required IReadOnlyList<DevelopmentEventResponse> Items { get; init; }
}

public sealed class ListDevelopmentArtifactsResponse
{
    public required IReadOnlyList<DevelopmentArtifactResponse> Items { get; init; }
}

public sealed class DevelopmentArtifactContentResponse
{
    public required DevelopmentArtifactResponse Artifact { get; init; }

    public required string Content { get; init; }
}

public sealed class DevelopmentNextActionResponse
{
    public required string Action { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid? AttemptId { get; init; }

    public required string TaskStatus { get; init; }

    public required string? Role { get; init; }
}

public sealed class DevelopmentPatchPreviewResponse
{
    public required string SubjectHash { get; init; }

    public required string PatchHash { get; init; }

    public required string ManifestHash { get; init; }

    public required string ExpectedResultHash { get; init; }

    public required string Patch { get; init; }

    public required IReadOnlyList<DevelopmentPatchPreviewFile> ChangedFiles { get; init; }
}

public sealed class DevelopmentApplyResponse
{
    public required Guid OperationId { get; init; }

    public required string Phase { get; init; }

    public required string Outcome { get; init; }

    public required string Status { get; init; }

    public required long Version { get; init; }

    public required long Sequence { get; init; }
}

public sealed class DevelopmentProfileDetectionRequest
{
    public Guid SelectedFolderId { get; init; }
}

/// <summary>
///     A detection proposal for a registered repository. Nothing here is authoritative — the operator confirms or
///     overrides it, and the confirmed choice is what gets snapshotted onto the project.
/// </summary>
public sealed class DevelopmentProfileDetectionResponse
{
    public required string ProfileId { get; init; }

    public required string? BuildTarget { get; init; }

    public required IReadOnlyList<string> Candidates { get; init; }
}
