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
    ///     Validates and dry-run-checks the run's exported patch against the host without mutating anything. Returns the
    ///     per-file plan, any host-path-safe rejections, and whether the patch contains a binary block.
    /// </summary>
    Task<NodePatchApplyPreview> PreviewAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Re-runs the full preview validation + dry-run check (TOCTOU defense; never blind-applies) and, only when the
    ///     check passes for every alias, applies the patch onto the host selected folders. Mutates nothing when the
    ///     re-check fails.
    /// </summary>
    Task<NodePatchApplyResult> ApplyApprovedAsync(NodePatchApplyRequest request, CancellationToken cancellationToken = default);
}
