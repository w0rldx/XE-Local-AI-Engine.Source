namespace XE_Local_AI_Engine.Tests.Agents;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the OPP-03 tool-approval policy compose. Proves the tighten-only invariant end to end: the
///     Permissive floor is identity on the catalog default; the node policy can only ADD approval (by category or by
///     tool name) and can never waive a default-on tool; unknown category names / false entries are no-ops; and a
///     null/empty settings object yields a policy equivalent to the floor.
/// </summary>
public sealed class NodeToolApprovalPolicyTests
{
    [Test]
    public void PermissiveFloor_IsIdentityOnCatalogDefault()
    {
        var floor = new PermissiveToolApprovalPolicy();

        AssertEx.True(floor.RequiresApproval("mcp__x__y", ToolCategory.Network, catalogDefault: true),
            "The floor must never waive a default-on tool.");
        AssertEx.False(floor.RequiresApproval("get_current_time", ToolCategory.ReadLocal, catalogDefault: false),
            "The floor must not add approval to a default-off tool.");
    }

    [Test]
    public void RequiresApproval_WhenCatalogDefaultTrue_StaysTrue()
    {
        // Tighten-only: an empty node policy can never loosen a default-on tool.
        var policy = new NodeToolApprovalPolicy(
            new Dictionary<ToolCategory, bool>(),
            new Dictionary<string, bool>(StringComparer.Ordinal));

        AssertEx.True(policy.RequiresApproval("mcp__x__y", ToolCategory.Network, catalogDefault: true));
    }

    [Test]
    public void RequiresApproval_WhenNodeCategoryTightens_FlipsFalseToTrue()
    {
        var policy = new NodeToolApprovalPolicy(
            new Dictionary<ToolCategory, bool> { [ToolCategory.Network] = true },
            new Dictionary<string, bool>(StringComparer.Ordinal));

        AssertEx.True(policy.RequiresApproval("mcp__x__y", ToolCategory.Network, catalogDefault: false),
            "A node category rule must tighten a default-off tool of that category.");
        AssertEx.False(policy.RequiresApproval("get_current_time", ToolCategory.ReadLocal, catalogDefault: false),
            "A node category rule must not affect tools of a different category.");
    }

    [Test]
    public void RequiresApproval_WhenPerToolOverrideTightens_FlipsFalseToTrue()
    {
        var policy = new NodeToolApprovalPolicy(
            new Dictionary<ToolCategory, bool>(),
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["list_files"] = true });

        AssertEx.True(policy.RequiresApproval("list_files", ToolCategory.ReadLocal, catalogDefault: false),
            "A per-tool-name rule must tighten just that tool.");
        AssertEx.False(policy.RequiresApproval("read_file", ToolCategory.ReadLocal, catalogDefault: false),
            "A per-tool-name rule must not affect a different tool.");
    }

    [Test]
    public void RequiresApproval_WhenCategoryEntryIsFalse_CannotLoosen()
    {
        // A stored false can never loosen: even if a category maps to false, a default-on tool stays on.
        var policy = new NodeToolApprovalPolicy(
            new Dictionary<ToolCategory, bool> { [ToolCategory.ReadLocal] = false },
            new Dictionary<string, bool>(StringComparer.Ordinal));

        AssertEx.True(policy.RequiresApproval("read_file", ToolCategory.ReadLocal, catalogDefault: true),
            "A false category entry must not waive a default-on tool.");
    }

    [Test]
    public void RequiresApproval_UnknownCategory_IsGovernedByItsOwnRuleOnly()
    {
        // The Unknown category is fail-closed at the OFFER layer (uncategorized tools default to Unknown), but the policy
        // itself only tightens Unknown when a rule says so; the catalog default still governs otherwise.
        var withRule = new NodeToolApprovalPolicy(
            new Dictionary<ToolCategory, bool> { [ToolCategory.Unknown] = true },
            new Dictionary<string, bool>(StringComparer.Ordinal));
        AssertEx.True(withRule.RequiresApproval("mystery", ToolCategory.Unknown, catalogDefault: false));

        var withoutRule = new NodeToolApprovalPolicy(
            new Dictionary<ToolCategory, bool>(),
            new Dictionary<string, bool>(StringComparer.Ordinal));
        AssertEx.False(withoutRule.RequiresApproval("mystery", ToolCategory.Unknown, catalogDefault: false));
    }

    [Test]
    public void FromSettings_WhenNull_YieldsIdentityPolicy()
    {
        var policy = NodeToolApprovalPolicy.FromSettings(settings: null);

        AssertEx.True(policy.RequiresApproval("mcp__x__y", ToolCategory.Network, catalogDefault: true));
        AssertEx.False(policy.RequiresApproval("get_current_time", ToolCategory.ReadLocal, catalogDefault: false));
    }

    [Test]
    public void FromSettings_ParsesCategoryNamesCaseInsensitively_AndIgnoresUnknownNames()
    {
        var settings = new NodeToolApprovalPolicySettings
        {
            Categories = new Dictionary<string, bool>
            {
                ["network"] = true,     // lower-case name must still bind to ToolCategory.Network
                ["writeexecute"] = false, // false entry is a no-op
                ["not-a-category"] = true // unknown name is ignored
            }
        };

        var policy = NodeToolApprovalPolicy.FromSettings(settings);

        AssertEx.True(policy.RequiresApproval("mcp__x__y", ToolCategory.Network, catalogDefault: false),
            "A case-insensitive category name must bind and tighten.");
        AssertEx.False(policy.RequiresApproval("run_in_agent_home", ToolCategory.WriteExecute, catalogDefault: false),
            "A false category entry must not tighten.");
    }

    [Test]
    public void FromSettings_ParsesPerToolOverrides_AndIgnoresBlankNames()
    {
        var settings = new NodeToolApprovalPolicySettings
        {
            Tools = new Dictionary<string, bool>
            {
                ["list_files"] = true,
                ["read_file"] = false, // no-op
                ["   "] = true          // blank name ignored
            }
        };

        var policy = NodeToolApprovalPolicy.FromSettings(settings);

        AssertEx.True(policy.RequiresApproval("list_files", ToolCategory.ReadLocal, catalogDefault: false));
        AssertEx.False(policy.RequiresApproval("read_file", ToolCategory.ReadLocal, catalogDefault: false));
    }
}
