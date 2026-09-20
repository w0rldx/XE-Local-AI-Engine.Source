namespace XE_Local_AI_Engine.Client.Services.Agents.Approval;

using Microsoft.Agents.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>
///     The single place the "can a session-scoped approval ever be remembered for THIS tool?" rule lives.
/// </summary>
/// <remarks>
///     Two callers share it and must not drift: <c>ToolApprovalCoordinator.TryResolveSessionApprovalKey</c> and the
///     node tool-catalog response, whose boolean keeps the chat card from offering a durable decision the node would
///     silently downgrade to "Once". The rule is expressed at TOOL-IDENTITY level, all a catalog entry knows, so it
///     is an upper bound: the runner narrows it further per call — see docs/wiki/04-agent-mode.md
///     ("Approval scoping") — and those narrowings only ever REMOVE eligibility.
/// </remarks>
public static class SessionApprovalEligibility
{
    /// <summary>
    ///     The operator's node-level "skill tools always prompt" switch.
    /// </summary>
    /// <remarks>
    ///     It lives on the concrete <c>NodeToolApprovalPolicy</c> rather than the cross-project
    ///     <see cref="IToolApprovalPolicy" /> contract, so both callers reach it through this one pattern match
    ///     instead of duplicating the cast.
    /// </remarks>
    public static bool IsSessionScopeDisabled(IToolApprovalPolicy approvalPolicy)
    {
        ArgumentNullException.ThrowIfNull(approvalPolicy);

        return approvalPolicy is NodeToolApprovalPolicy { SkillSessionScopeDisabled: true };
    }

    /// <summary>Whether the name belongs to a node-local user-defined custom tool (the reserved <c>custom__</c> prefix).</summary>
    public static bool IsCustomToolName(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && toolName.StartsWith(CustomToolValidation.ToolNamePrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Whether the name is one of MAF's two session-scopable skill tools.
    /// </summary>
    /// <remarks>
    ///     <c>run_skill_script</c> is deliberately absent: a durable approval on script execution is the one decision
    ///     an operator re-makes every time.
    /// </remarks>
    public static bool IsSkillToolName(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return false;
        }

#pragma warning disable MAAI001 // Agent Skills is [Experimental] in Microsoft.Agents.AI; the same scoped suppression the provider call sites use.
        return string.Equals(toolName, AgentSkillsProvider.LoadSkillToolName, StringComparison.Ordinal)
               || string.Equals(toolName, AgentSkillsProvider.ReadSkillResourceToolName, StringComparison.Ordinal);
#pragma warning restore MAAI001
    }

    /// <summary>
    ///     The tool-identity answer the catalog exposes: the two skill tools, plus a custom tool in <c>Fixed</c>
    ///     mode only.
    /// </summary>
    /// <remarks>
    ///     A <c>Parameterized</c> custom tool is once-or-deny, because one click must not grant open-ended,
    ///     model-chosen execution.
    /// </remarks>
    /// <param name="toolName">The executable tool name.</param>
    /// <param name="isFixedCustomTool">Only meaningful for a <c>custom__</c> name: whether that tool runs a verbatim, operator-authored invocation.</param>
    public static bool IsToolEligible(string? toolName, bool isFixedCustomTool) =>
        IsCustomToolName(toolName) ? isFixedCustomTool : IsSkillToolName(toolName);

    /// <summary>
    ///     <see cref="IsToolEligible(string?,bool)" /> with the node's always-prompt switch applied. This is the answer
    ///     the tool-catalog response carries; the invocation runner evaluates the switch once at construction instead
    ///     and calls the policy-free overload per call.
    /// </summary>
    public static bool IsToolEligible(IToolApprovalPolicy approvalPolicy, string? toolName, bool isFixedCustomTool) =>
        !IsSessionScopeDisabled(approvalPolicy) && IsToolEligible(toolName, isFixedCustomTool);
}
