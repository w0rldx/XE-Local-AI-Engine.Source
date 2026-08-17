namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Agents.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the ONE session-approval eligibility predicate shared by
///     <c>ToolApprovalCoordinator.TryResolveSessionApprovalKey</c> (which turns an eligible call into a memo key) and the node
///     tool-catalog response (which exposes the same answer so the chat card only offers "Approve for this session"
///     where the node honors it). The two must never drift, so the rule is pinned here once.
/// </summary>
public sealed class SessionApprovalEligibilityTests
{
#pragma warning disable MAAI001 // Agent Skills is [Experimental] in Microsoft.Agents.AI.
    private const string LoadSkillToolName = AgentSkillsProvider.LoadSkillToolName;

    private const string ReadSkillResourceToolName = AgentSkillsProvider.ReadSkillResourceToolName;
#pragma warning restore MAAI001

    [Test]
    public void IsToolEligible_AllowsTheTwoSkillTools_AndNothingElseUnprefixed()
    {
        AssertEx.True(SessionApprovalEligibility.IsToolEligible(LoadSkillToolName, isFixedCustomTool: false));
        AssertEx.True(SessionApprovalEligibility.IsToolEligible(ReadSkillResourceToolName, isFixedCustomTool: false));

        // The tools the card used to offer session scope for while the node silently treated the decision as "Once":
        // an MCP tool, the agent-home runner, a plain built-in, and run_skill_script (hard-excluded by design).
        AssertEx.False(SessionApprovalEligibility.IsToolEligible("mcp__filesystem__read_file", isFixedCustomTool: false));
        AssertEx.False(SessionApprovalEligibility.IsToolEligible("run_in_agent_home", isFixedCustomTool: false));
        AssertEx.False(SessionApprovalEligibility.IsToolEligible("GetCurrentTime", isFixedCustomTool: false));
        AssertEx.False(SessionApprovalEligibility.IsToolEligible("run_skill_script", isFixedCustomTool: false));
        AssertEx.False(SessionApprovalEligibility.IsToolEligible(toolName: null, isFixedCustomTool: false));
        AssertEx.False(SessionApprovalEligibility.IsToolEligible(string.Empty, isFixedCustomTool: false));
    }

    [Test]
    public void IsToolEligible_ForACustomTool_TurnsOnFixedModeOnly()
    {
        // A Fixed custom tool runs a verbatim, operator-authored invocation, so one grant is bounded; a Parameterized
        // one is once-or-deny because a single click must not authorise open-ended, model-chosen execution. The
        // isFixedCustomTool flag is consulted ONLY for a custom__ name — the skill answer must not depend on it.
        AssertEx.True(SessionApprovalEligibility.IsToolEligible("custom__deploy", isFixedCustomTool: true));
        AssertEx.False(SessionApprovalEligibility.IsToolEligible("custom__deploy", isFixedCustomTool: false));
        AssertEx.False(SessionApprovalEligibility.IsToolEligible("GetCurrentTime", isFixedCustomTool: true));
    }

    [Test]
    public void IsToolEligible_WhenTheOperatorDisablesSessionScope_IsFalseForEveryTool()
    {
        var disabled = NodeToolApprovalPolicy.FromSettings(new NodeToolApprovalPolicySettings
        {
            DisableSkillSessionScope = true
        });

        AssertEx.False(SessionApprovalEligibility.IsSessionScopeDisabled(new PermissiveToolApprovalPolicy()),
            "A policy without the node switch leaves session scope available (today's behaviour).");
        AssertEx.True(SessionApprovalEligibility.IsSessionScopeDisabled(disabled));

        AssertEx.False(SessionApprovalEligibility.IsToolEligible(disabled, LoadSkillToolName, isFixedCustomTool: false));
        AssertEx.False(SessionApprovalEligibility.IsToolEligible(disabled, "custom__deploy", isFixedCustomTool: true));

        // Same two tools, switch off: the always-prompt knob is the only difference.
        var enabled = NodeToolApprovalPolicy.FromSettings(settings: null);
        AssertEx.True(SessionApprovalEligibility.IsToolEligible(enabled, LoadSkillToolName, isFixedCustomTool: false));
        AssertEx.True(SessionApprovalEligibility.IsToolEligible(enabled, "custom__deploy", isFixedCustomTool: true));
    }
}
