namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     No-op <see cref="IToolApprovalPolicy" /> floor that applies no node-level tightening, so a host without a
///     node-configured policy adds no approval requirement of its own. Mirrors
///     <c>NoOpGpuModelLoadAdmission</c>.
/// </summary>
/// <remarks>
///     Wired via <c>TryAddSingleton</c> so a provider-only host or a test always resolves a policy; the real
///     <c>NodeToolApprovalPolicy</c>, registered by the composition root with a plain <c>AddSingleton</c>, wins under
///     last-registration-wins. The one non-identity term is the fail-closed <see cref="ToolCategory.Unknown" /> rule
///     both policies share — an uncategorized tool is never auto-executed — kept here for defense in depth, and
///     inert because no offered tool ships <see cref="ToolCategory.Unknown" />.
/// </remarks>
public sealed class PermissiveToolApprovalPolicy : IToolApprovalPolicy
{
    /// <inheritdoc />
    public bool RequiresApproval(string toolName, ToolCategory category, bool catalogDefault)
    {
        // Identity on the catalog default, with the shared fail-closed Unknown rule: an uncategorized tool always
        // requires approval. No offered tool ships Unknown, so the answer equals the catalog default.
        return catalogDefault || category == ToolCategory.Unknown;
    }
}
