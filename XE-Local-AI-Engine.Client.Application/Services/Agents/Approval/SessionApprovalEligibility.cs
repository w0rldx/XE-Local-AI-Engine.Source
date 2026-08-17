namespace XE_Local_AI_Engine.Client.Services.Agents.Approval;

using Microsoft.Agents.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>
///     The single place the "can a session-scoped approval ever be remembered for THIS tool?" rule lives. Two callers
///     share it and must not drift: <c>ToolApprovalCoordinator.TryResolveSessionApprovalKey</c>, which turns an eligible call
///     into an <see cref="ApprovalMemoKey" />, and the node tool-catalog response, which exposes the same answer as a
///     boolean so the chat approval card only offers "Approve for this session" where the node will honor it. Before
///     the boolean existed the card offered session scope on EVERY approval and silently behaved as "Once" for the
///     ineligible majority — a UI that promises a durable decision it does not make is worse than one that never
///     offers it.
///     <para>
///         The rule is deliberately expressed at the TOOL-IDENTITY level, which is all a catalog entry knows. The
///         runner applies two further per-CALL narrowings the catalog cannot see and that only ever REMOVE eligibility:
///         the named skill must be carried by the package and must not be <c>Origin.Imported</c>, and
///         <c>read_skill_resource</c> must name its resource. So the boolean is an upper bound — never a promise.
///     </para>
/// </summary>
public static class SessionApprovalEligibility
{
    /// <summary>
    ///     The operator's node-level "skill tools always prompt" switch. It lives on the concrete
    ///     <c>NodeToolApprovalPolicy</c> rather than on the cross-project <see cref="IToolApprovalPolicy" /> contract
    ///     (see that type), so both callers reach it through this one pattern match instead of duplicating the cast.
    /// </summary>
    public static bool IsSessionScopeDisabled(IToolApprovalPolicy approvalPolicy)
    {
        ArgumentNullException.ThrowIfNull(approvalPolicy);

        return approvalPolicy is NodeToolApprovalPolicy { SkillSessionScopeDisabled: true };
    }

    /// <summary>Whether the name belongs to a node-local user-defined custom tool (the reserved <c>custom__</c> prefix).</summary>
    public static bool IsCustomToolName(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && toolName.StartsWith(CustomToolValidation.ToolNamePrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Whether the name is one of MAF's two session-scopable skill tools. <c>run_skill_script</c> is deliberately
    ///     absent: a durable approval on script execution is the one decision an operator re-makes every time.
    /// </summary>
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
    ///     The tool-identity answer the catalog exposes. A custom tool qualifies only in <c>Fixed</c> mode — a
    ///     <c>Parameterized</c> tool is once-or-deny, because one click must not grant open-ended, model-chosen
    ///     execution — and no other tool qualifies except the two skill tools.
    /// </summary>
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
