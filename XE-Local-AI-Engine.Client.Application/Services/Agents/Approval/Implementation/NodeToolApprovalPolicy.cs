namespace XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The node-configured <see cref="IToolApprovalPolicy" />: a per-<see cref="ToolCategory" /> map plus optional
///     per-tool-name overrides, composed ON TOP of each tool's catalog approval flag.
/// </summary>
/// <remarks>
///     TIGHTEN-ONLY — it can only turn a non-approval tool into an approval-requiring one, never the reverse — and it
///     wins over the <see cref="PermissiveToolApprovalPolicy" /> floor through the composition root's
///     <c>AddSingleton</c>. The maps are captured at construction, so evaluation is a synchronous dictionary lookup
///     on the hot resolve path and operator edits apply on the next node restart.
/// </remarks>
internal sealed class NodeToolApprovalPolicy : IToolApprovalPolicy
{
    private readonly IReadOnlyDictionary<ToolCategory, bool> _categoryPolicy;
    private readonly IReadOnlyDictionary<string, bool> _toolOverrides;

    public NodeToolApprovalPolicy(IReadOnlyDictionary<ToolCategory, bool> categoryPolicy,
        IReadOnlyDictionary<string, bool> toolOverrides,
        bool skillSessionScopeDisabled = false)
    {
        _categoryPolicy = categoryPolicy ?? throw new ArgumentNullException(nameof(categoryPolicy));
        _toolOverrides = toolOverrides ?? throw new ArgumentNullException(nameof(toolOverrides));
        SkillSessionScopeDisabled = skillSessionScopeDisabled;
    }

    /// <summary>
    ///     The operator's "skill tools always prompt" switch: while set, the runner remembers no session-scoped
    ///     approval, so every skill-tool call raises its own card.
    /// </summary>
    /// <remarks>
    ///     Not on <see cref="IToolApprovalPolicy" />, which is the cross-project contract for one yes/no verdict on
    ///     ONE call, and not on <c>INodeRuntimeSettings</c>, because it belongs with the rest of the approval policy
    ///     an operator edits in one block of <c>node-settings.json</c>.
    /// </remarks>
    public bool SkillSessionScopeDisabled { get; }

    /// <inheritdoc />
    public bool RequiresApproval(string toolName, ToolCategory category, bool catalogDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        // TIGHTEN-ONLY: an OR can only ADD approval, never clear it, so no branch returns false on a true catalog
        // default. An Unknown-category tool ALWAYS requires approval, so a new uncategorized tool never auto-executes.
        return catalogDefault
               || category == ToolCategory.Unknown
               || _categoryPolicy.GetValueOrDefault(category, defaultValue: false)
               || _toolOverrides.GetValueOrDefault(toolName, defaultValue: false);
    }

    /// <summary>
    ///     Builds a policy from the persisted node-default settings.
    /// </summary>
    /// <remarks>
    ///     Unknown category names are ignored (parsed case-insensitively) and only entries that ADD approval are
    ///     retained, so the composed policy is purely a tighten-set and a stored <see langword="false" /> can never
    ///     loosen a tool. A null or empty settings object yields an empty policy equivalent to
    ///     <see cref="PermissiveToolApprovalPolicy" />: identity on the catalog default.
    /// </remarks>
    public static NodeToolApprovalPolicy FromSettings(NodeToolApprovalPolicySettings? settings)
    {
        var categoryPolicy = new Dictionary<ToolCategory, bool>();
        if (settings?.Categories is { } categories)
        {
            foreach (var (name, requiresApproval) in categories)
            {
                if (requiresApproval && Enum.TryParse<ToolCategory>(name, ignoreCase: true, out var category))
                {
                    categoryPolicy[category] = true;
                }
            }
        }

        var toolOverrides = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (settings?.Tools is { } tools)
        {
            foreach (var (name, requiresApproval) in tools)
            {
                if (requiresApproval && !string.IsNullOrWhiteSpace(name))
                {
                    toolOverrides[name] = true;
                }
            }
        }

        return new NodeToolApprovalPolicy(categoryPolicy, toolOverrides, settings?.DisableSkillSessionScope ?? false);
    }
}
