namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Worker-side, approval-gated host patch apply: lands an exported <c>changes.patch</c> onto the real host
///     selected folders.
/// </summary>
/// <remarks>
///     A sandbox-relative <c>a/&lt;alias&gt;/…</c> or <c>b/&lt;alias&gt;/…</c> prefix maps back to its trusted host root
///     via <see cref="Workspace.ISelectedFolderResolver" />, and the patch applies only under that root —
///     traversal-rejected, binary-rejected by default. The <see cref="PreviewAsync" /> / <see cref="ApplyApprovedAsync" /> split
///     is the approval gate: preview, explicit human confirm, then apply. It is a locked, user-driven worker-local
///     surface, never a model-invoked tool, and the apply re-runs the full validation and dry-run check itself.
/// </remarks>
public interface INodePatchApplyService
{
    /// <summary>
    ///     Validates and dry-run-checks the run's exported patch against the host without mutating anything. Returns
    ///     the per-file plan, any rejections, whether the patch contains a binary block, and the SHA-256 of the bytes
    ///     it validated.
    /// </summary>
    Task<NodePatchApplyPreview> PreviewAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Re-runs the full preview validation and dry-run check (TOCTOU defence) and applies only when every alias
    ///     checks clean; a failed re-check mutates nothing. <see cref="NodePatchApplyRequest.ExpectedPatchSha256" />
    ///     binds the approval to the diff that was shown. Applies are serialized node-wide.
    /// </summary>
    Task<NodePatchApplyResult> ApplyApprovedAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default);
}
