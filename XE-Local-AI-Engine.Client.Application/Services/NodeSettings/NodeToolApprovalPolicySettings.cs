namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The persisted node-default tool-approval policy, stored as JSON inside <see cref="StoredNodeSettings" /> and
///     TIGHTEN-ONLY: an entry can only ADD an approval requirement, never waive a tool's own catalog flag.
/// </summary>
/// <remarks>
///     Absent, the default, there is no node tightening at all. The maps are deliberately string-keyed so
///     <c>node-settings.json</c> stays human-editable: <see cref="Categories" /> by <c>ToolCategory</c> NAME and
///     <see cref="Tools" /> by exact tool name. A <see langword="true" /> value requires approval, a
///     <see langword="false" /> one is a no-op that cannot loosen, and unknown category and tool names are ignored when
///     the policy is composed. Edits apply on the next node restart, the policy being read once at composition.
/// </remarks>
public sealed record NodeToolApprovalPolicySettings
{
    /// <summary>Per-<c>ToolCategory</c>-name approval requirement (<see langword="true" /> = require approval).</summary>
    public IReadOnlyDictionary<string, bool>? Categories { get; init; }

    /// <summary>Per-tool-name approval requirement (<see langword="true" /> = require approval), overriding the category rule.</summary>
    public IReadOnlyDictionary<string, bool>? Tools { get; init; }

    /// <summary>
    ///     Turns OFF session-scoped approvals for the skill tools entirely; <see langword="false" />, the default,
    ///     leaves them available.
    /// </summary>
    /// <remarks>
    ///     This is the operator's "skill tools always prompt" switch: with it set, an "approve for this session"
    ///     decision is never remembered and every <c>load_skill</c> or <c>read_skill_resource</c> call raises its own
    ///     approval card. Like the maps above it can only TIGHTEN — no setting makes an approval last longer than the
    ///     operator asked for.
    /// </remarks>
    public bool DisableSkillSessionScope { get; init; }
}
