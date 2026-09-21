namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1;

// Requests. The run id is a route parameter and binds by name, so the property name here is the wire name.

/// <summary>The run whose exported <c>changes.patch</c> is to be previewed.</summary>
public sealed class AgentHomePatchPreviewRequest
{
    public string RunId { get; init; } = string.Empty;
}

/// <summary>
///     The approval. <see cref="PatchSha256" /> is the hash the preview reported for the bytes it validated, and is
///     REQUIRED: without it an apply could land a <c>changes.patch</c> that replaced the one the operator read.
/// </summary>
public sealed class AgentHomePatchApplyRequest
{
    public string RunId { get; init; } = string.Empty;

    /// <summary>
    ///     <c>required</c> is what puts this in the schema's <c>required</c> array: the schema processor does not read
    ///     requiredness from the validator's shape rule, so a generated client could otherwise omit it and take a 400.
    /// </summary>
    public required string PatchSha256 { get; init; }
}

// Responses. Every path is folder-relative (<alias>/<rel>) and every message is the service's own redacted string:
// nothing on this surface carries a host path.

/// <summary>What an apply would do, and whether it can be attempted at all.</summary>
public sealed class AgentHomePatchPreviewResponse
{
    /// <summary>Whether every alias sub-patch checked clean AND nothing was rejected. Gates the Apply call.</summary>
    public required bool CanApply { get; init; }

    /// <summary>The per-file plan, one entry per changed file.</summary>
    public required IReadOnlyList<AgentHomePatchFileDto> Files { get; init; }

    /// <summary>Why the patch cannot apply, in the service's own host-path-safe words. Empty when it can.</summary>
    public required IReadOnlyList<AgentHomePatchRejectionDto> Rejections { get; init; }

    /// <summary>
    ///     Patch targets that already carry local changes in the operator's folder. A warning to read before
    ///     approving, never a refusal: it does not gate <see cref="CanApply" />.
    /// </summary>
    public required IReadOnlyList<AgentHomePatchDirtyTargetDto> DirtyTargets { get; init; }

    /// <summary>Whether at least one folder's local state could not be read, so the warning above is incomplete.</summary>
    public required bool DirtyCheckUnavailable { get; init; }

    /// <summary>Whether the patch contains a binary block. Binary is refused unless the node opted in.</summary>
    public required bool ContainsBinary { get; init; }

    /// <summary>
    ///     SHA-256 (lowercase hex) of the exact patch bytes this preview validated. An apply must send it back; a
    ///     mismatch is a 409. Null when the patch was never read (over the size budget, or unreadable), where
    ///     <see cref="CanApply" /> is false anyway.
    /// </summary>
    public required string? PatchSha256 { get; init; }
}

/// <summary>What the apply did.</summary>
public sealed class AgentHomePatchApplyResponse
{
    /// <summary>The files written to the host, folder-relative.</summary>
    public required IReadOnlyList<AgentHomePatchFileDto> AppliedFiles { get; init; }
}

/// <summary>One refusal. <see cref="Path" /> names the refused entry when it has one that is safe to show.</summary>
public sealed class AgentHomePatchRejectionDto
{
    public required string Reason { get; init; }

    /// <summary>Folder-relative (<c>&lt;alias&gt;/&lt;rel&gt;</c>), or null for a refusal about the patch as a whole.</summary>
    public required string? Path { get; init; }
}

/// <summary>One patch target that already differs from its committed state in the operator's folder.</summary>
public sealed class AgentHomePatchDirtyTargetDto
{
    /// <summary>Folder-relative (<c>&lt;alias&gt;/&lt;rel&gt;</c>); never a host path.</summary>
    public required string Path { get; init; }

    /// <summary><c>modified</c>, <c>staged</c> or <c>untracked</c>.</summary>
    public required string State { get; init; }
}

/// <summary>One changed file. Carries no host path — <see cref="Alias" /> names the selected folder instead.</summary>
public sealed class AgentHomePatchFileDto
{
    public required string Alias { get; init; }

    public required string RelativePath { get; init; }

    /// <summary><c>added</c>, <c>modified</c>, <c>deleted</c>, <c>renamed</c> or <c>copied</c>.</summary>
    public required string ChangeType { get; init; }

    public required int Added { get; init; }

    public required int Removed { get; init; }
}
