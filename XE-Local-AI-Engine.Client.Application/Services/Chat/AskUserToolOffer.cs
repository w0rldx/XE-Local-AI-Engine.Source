namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     The single place that unions <c>ask_user</c> into a resolved tool set, shared by every seam that narrows the
///     loopback offer before it reaches the model.
///     <para>
///         WHY a shared helper rather than four copies: <c>ask_user</c> is available on EVERY interactive tool-enabled
///         turn, NOT gated on an agent's <c>AllowedToolNames</c> — an agent that can use tools at all can always ask its
///         operator a question. That availability rule has to hold at each seam that builds a tool set
///         (<c>AgentDefinitionResolver.ProjectAllowedTools</c> for a bound agent and for the mode-off Default Assistant,
///         <c>OrchestrationResolver.ProjectAllowedTools</c> for participants, and the <c>resolved == null</c> fallbacks
///         in the chat stream/regeneration services when a conversation's bound agent was deleted). Miss one and the
///         tool is silently absent on that path only — the failure mode docs/agent-knowledge.md §4 records for the
///         approval-policy compose, which has exactly the same seam set.
///     </para>
/// </summary>
internal static class AskUserToolOffer
{
    /// <summary>
    ///     Returns <paramref name="projected" /> with <c>ask_user</c> guaranteed present, lifting its descriptor from
    ///     <paramref name="offered" /> (the un-narrowed offer this seam projected from) and re-composing its approval
    ///     flag through <paramref name="toolApprovalPolicy" />, exactly as the seam does for every other tool. Idempotent
    ///     and allocation-free when the tool is already there, so a seam whose projection IS the whole offer can call it
    ///     as a cheap invariant guard.
    ///     <para>
    ///         The compose is TIGHTEN-ONLY like every other seam's: the catalog default is already <c>true</c>
    ///         (structural — it is what routes the call to the out-of-stream human round-trip), and
    ///         <see cref="IToolApprovalPolicy" /> may never return <c>false</c> for a <c>true</c> default, so this can
    ///         only ever keep the tool approval-gated. Per-agent <c>ToolApprovals</c> are deliberately not consulted:
    ///         they too can only ADD approval, so they have nothing to add here.
    ///     </para>
    ///     <para>
    ///         When <paramref name="offered" /> does not contain <c>ask_user</c> the projection is returned unchanged
    ///         rather than fabricating a descriptor. This matches the surrounding house rule that a seam never hands the
    ///         model a tool the node did not offer, and it keeps the offer provider the single authority on the
    ///         descriptor's schema, approval flag and deterministic id.
    ///     </para>
    /// </summary>
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
