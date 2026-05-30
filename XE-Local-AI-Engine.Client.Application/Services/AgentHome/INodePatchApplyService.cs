namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Worker-side, approval-gated host patch apply (AgentHome plan §9.2, Marker L). Lands Marker G's exported
///     <c>changes.patch</c> onto the real host selected folders, mapping each sandbox-relative <c>a/&lt;alias&gt;/…</c>
///     / <c>b/&lt;alias&gt;/…</c> prefix back to its trusted host root via <see cref="Workspace.ISelectedFolderResolver" />
///     and applying only under that root (traversal-rejected, binary-rejected by default).
/// </summary>
/// <remarks>
///     The <see cref="PreviewAsync" /> / <see cref="ApplyApprovedAsync" /> split IS the §9.2 approval gate: a caller
///     obtains a preview, surfaces it for an explicit human confirm, then calls <see cref="ApplyApprovedAsync" />. The
///     surface is a user-driven worker-local action — never a model-invoked tool (locked surface decision); the model
///     cannot trigger host mutation. <see cref="ApplyApprovedAsync" /> re-runs the full validation + dry-run check
///     internally, so it can never blind-apply.
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
