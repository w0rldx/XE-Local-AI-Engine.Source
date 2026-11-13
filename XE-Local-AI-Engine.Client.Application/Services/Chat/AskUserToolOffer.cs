namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     The single place that unions <c>ask_user</c> into a resolved tool set, shared by every seam that narrows the
///     loopback offer before it reaches the model.
/// </summary>
/// <remarks>
///     <c>ask_user</c> is available on EVERY interactive tool-enabled turn and is NOT gated on an agent's
///     <c>AllowedToolNames</c>: one that can use tools at all can always ask its operator a question. That has to hold
///     at each of the four seams building a tool set — <c>AgentDefinitionResolver.ProjectAllowedTools</c>,
///     <c>OrchestrationResolver.ProjectAllowedTools</c> and the two unbound fallbacks — and missing one leaves the tool
///     silently absent there: the failure <c>docs/agent-knowledge.md</c> §4 records for the same approval-policy seams.
/// </remarks>
internal static class AskUserToolOffer
{
    /// <summary>
    ///     Returns <paramref name="projected" /> with <c>ask_user</c> guaranteed present, lifting its descriptor from
    ///     <paramref name="offered" /> and re-composing its approval flag as the seam does for every other tool.
    /// </summary>
    /// <remarks>
    ///     It is idempotent and allocation-free when the tool is already there, so a seam whose projection IS the whole
    ///     offer can call it as a cheap invariant guard. The compose is TIGHTEN-ONLY, so it can only keep the tool
    ///     approval-gated. When <paramref name="offered" /> does not carry <c>ask_user</c> the projection is returned
    ///     unchanged rather than fabricating a descriptor, keeping the offer provider the single authority on its
    ///     schema, approval flag and deterministic id.
    /// </remarks>
    /// <param name="projected">The seam's narrowed tool set (offer ∩ allowed names, approval flags already composed).</param>
    /// <param name="offered">The un-narrowed offer <paramref name="projected" /> was derived from.</param>
    /// <param name="toolApprovalPolicy">The node's tighten-only approval policy.</param>
    public static IReadOnlyList<AllowedToolDto> EnsureOffered(IReadOnlyList<AllowedToolDto> projected,
        IReadOnlyList<AllowedToolDto> offered,
        IToolApprovalPolicy toolApprovalPolicy)
    {
        ArgumentNullException.ThrowIfNull(projected);
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(toolApprovalPolicy);

        if (projected.Any(IsAskUser))
        {
            return projected;
        }

        var askUser = offered.FirstOrDefault(IsAskUser);
        if (askUser is null)
        {
            return projected;
        }

        return
        [
            .. projected,
            askUser with
            {
                RequiresApproval = toolApprovalPolicy.RequiresApproval(askUser.Name, askUser.Category, askUser.RequiresApproval)
            }
        ];
    }

    private static bool IsAskUser(AllowedToolDto tool)
    {
        return string.Equals(tool.Name, AskUserTool.ToolName, StringComparison.Ordinal);
    }
}
