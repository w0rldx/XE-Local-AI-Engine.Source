namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The persisted node-default tool-approval policy, stored as JSON inside <see cref="StoredNodeSettings" />.
///     It is TIGHTEN-ONLY: an entry can only ADD an approval requirement to a tool that would otherwise auto-execute; it
///     can never waive a tool's own catalog approval flag. <see langword="null" /> / absent (the default) means no node
///     tightening at all, so the resolver behaves byte-for-byte as it did before this feature.
///     <para>
///         The maps are deliberately string-keyed so <c>node-settings.json</c> stays human-editable: <see cref="Categories" />
///         is keyed by <c>ToolCategory</c> NAME (e.g. <c>"Network"</c>, <c>"WriteExecute"</c>) and <see cref="Tools" /> by
///         exact tool name (e.g. <c>"mcp__weather__get_forecast"</c>). A value of <see langword="true" /> requires
///         approval; a <see langword="false" /> value is a no-op (it cannot loosen). Unknown category names and unknown
///         tool names are ignored when the policy is composed. Edits apply on the next node restart (read once at
///         composition, matching the other migrated node knobs).
///     </para>
/// </summary>
public sealed record NodeToolApprovalPolicySettings
{
    /// <summary>Per-<c>ToolCategory</c>-name approval requirement (<see langword="true" /> = require approval).</summary>
    public IReadOnlyDictionary<string, bool>? Categories { get; init; }

    /// <summary>Per-tool-name approval requirement (<see langword="true" /> = require approval), overriding the category rule.</summary>
    public IReadOnlyDictionary<string, bool>? Tools { get; init; }

    /// <summary>
    ///     Turns OFF session-scoped approvals for the skill tools entirely (<see langword="false" />, the default, leaves
    ///     them available). This is the operator's "skill tools always prompt" switch: with it set, an
    ///     "approve for this session" decision is never remembered and every <c>load_skill</c> / <c>read_skill_resource</c>
    ///     call raises its own approval card. Like the maps above it can only TIGHTEN — there is no setting that makes an
    ///     approval last longer than the operator asked for.
    /// </summary>
    public bool DisableSkillSessionScope { get; init; }
}
