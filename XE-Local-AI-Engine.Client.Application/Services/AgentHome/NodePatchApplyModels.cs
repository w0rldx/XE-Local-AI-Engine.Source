namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Request for <see cref="INodePatchApplyService" />. Identifies the run whose exported
///     <c>runs/&lt;RunId&gt;/patches/changes.patch</c> is to be previewed/applied. <see cref="RunId" /> is untrusted
///     and shape-validated before any path is composed.
/// </summary>
public sealed record NodePatchApplyRequest
{
    /// <summary>The run id; resolves <c>&lt;RootPath&gt;/runs/&lt;RunId&gt;/patches/changes.patch</c>.</summary>
    public required string RunId { get; init; }

    /// <summary>
    ///     The SHA-256 (lowercase hex) a preview reported for the bytes it validated. When present, the apply refuses
    ///     a patch that hashes differently, so the operator approves the diff they were shown. Omitted, re-validation
    ///     is the whole defence.
    /// </summary>
    public string? ExpectedPatchSha256 { get; init; }
}

/// <summary>
///     Result of <see cref="INodePatchApplyService.PreviewAsync" /> — a non-mutating dry-run plan. All paths are
///     folder-relative (<c>&lt;alias&gt;/&lt;rel&gt;</c>); rejection strings carry no host path.
/// </summary>
public sealed record NodePatchApplyPreview
{
    /// <summary>
    ///     <see langword="true" /> when every alias sub-patch passes <c>git apply --check</c> AND there are no
    ///     rejections.
    /// </summary>
    public required bool CanApply { get; init; }

    /// <summary>The per-file plan, one entry per changed file with its alias, folder-relative path, and +/- counts.</summary>
    public required IReadOnlyList<PatchApplyFileEntry> Files { get; init; }

    /// <summary>Host-path-safe reasons the patch cannot apply (unknown alias, traversal, binary-not-allowed, conflict).</summary>
    public required IReadOnlyList<string> Rejections { get; init; }

    /// <summary>Whether the patch contains a binary block (detected in the patch text, not via git).</summary>
    public bool ContainsBinary { get; init; }

    /// <summary>
    ///     SHA-256 (lowercase hex) of the exact patch bytes this preview validated, or <see langword="null" /> when no
    ///     patch could be read. An apply that echoes it back in <see cref="NodePatchApplyRequest.ExpectedPatchSha256" />
    ///     is refused if the bytes on disk have changed since.
    /// </summary>
    public string? PatchSha256 { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the run has no readable <c>changes.patch</c> at all (unknown run, missing or
    ///     empty file). Distinguishes "nothing to review" from "a patch that will not apply", which is a different
    ///     answer for a caller that maps this onto a status code.
    /// </summary>
    public bool PatchMissing { get; init; }
}

/// <summary>
///     Result of <see cref="INodePatchApplyService.ApplyApprovedAsync" />. All paths are folder-relative; rejection
///     strings carry no host path.
/// </summary>
public sealed record NodePatchApplyResult
{
    /// <summary><see langword="true" /> when the patch was applied to the host.</summary>
    public required bool Applied { get; init; }

    /// <summary>The files written to the host, folder-relative.</summary>
    public required IReadOnlyList<PatchApplyFileEntry> AppliedFiles { get; init; }

    /// <summary>Host-path-safe reasons the apply was rejected or could not complete.</summary>
    public required IReadOnlyList<string> Rejections { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the pre-apply check passed for every alias but a later write failed (rare race),
    ///     leaving some aliases applied and others not. <see cref="AppliedFiles" /> reports what landed.
    /// </summary>
    public bool PartiallyApplied { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the run has no readable <c>changes.patch</c> at all. The twin of
    ///     <see cref="NodePatchApplyPreview.PatchMissing" />.
    /// </summary>
    public bool PatchMissing { get; init; }
}

/// <summary>A single changed file in a patch apply preview/result. Carries no alias prefix and no host path.</summary>
public sealed record PatchApplyFileEntry
{
    /// <summary>The selected-folder alias the file belongs to.</summary>
    public required string Alias { get; init; }

    /// <summary>The folder-relative path (alias prefix stripped); never a host path.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The change type: <c>added</c>, <c>modified</c>, <c>deleted</c>, <c>renamed</c>, or <c>copied</c>.</summary>
    public required string ChangeType { get; init; }

    /// <summary>Lines added, from <c>git apply --numstat</c>.</summary>
    public int Added { get; init; }

    /// <summary>Lines removed, from <c>git apply --numstat</c>.</summary>
    public int Removed { get; init; }
}
