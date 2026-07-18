namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The persisted node-default tool-approval policy (OPP-03), stored as JSON inside <see cref="StoredNodeSettings" />.
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
}
