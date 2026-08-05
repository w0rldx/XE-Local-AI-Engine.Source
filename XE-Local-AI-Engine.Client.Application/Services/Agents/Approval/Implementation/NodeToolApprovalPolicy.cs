namespace XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The node-configured <see cref="IToolApprovalPolicy" />. Composes the node-default policy — a
///     per-<see cref="ToolCategory" /> map plus optional per-tool-name overrides — ON TOP of each tool's catalog approval
///     flag, TIGHTEN-ONLY: it can only turn a non-approval tool into an approval-requiring one, never the reverse. It wins
///     over the <see cref="PermissiveToolApprovalPolicy" /> floor via the composition root's plain <c>AddSingleton</c>.
///     <para>
///         The maps are captured at construction (seeded once from node settings at composition, like the tool-capable
///         allow-list on <c>LocalToolOfferProvider</c>), so evaluation is a synchronous dictionary lookup on the hot
///         resolve path. Operator edits apply on the next node restart.
///     </para>
/// </summary>
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
    ///     The operator's "skill tools always prompt" switch: when <see langword="true" />, the invocation runner never
    ///     remembers a session-scoped approval, so every skill-tool call raises its own approval card. It lives here
    ///     rather than on <see cref="IToolApprovalPolicy" /> because that interface is the cross-project AI.Agent
    ///     contract for a single yes/no verdict on ONE call — a node-only, duration-of-a-conversation knob has no place
    ///     in it — and rather than on <c>INodeRuntimeSettings</c> because it belongs with the rest of the approval
    ///     policy an operator edits in one block of <c>node-settings.json</c>.
    /// </summary>
    public bool SkillSessionScopeDisabled { get; }

    /// <inheritdoc />
    public bool RequiresApproval(string toolName, ToolCategory category, bool catalogDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        // TIGHTEN-ONLY: OR the catalog default with the fail-closed Unknown rule, the node-default-by-category rule, and
        // the per-tool-name override. The expression can only ADD approval (never clear it): if the catalog default is
        // already true it stays true, and any additional term can only push a default-off tool to true. There is no branch
        // that returns false when the catalog default is true. An Unknown-category tool ALWAYS requires approval
        // (fail-closed), honoring the IToolApprovalPolicy / ToolCategory.Unknown contract so a new, uncategorized tool
        // never silently auto-executes.
        return catalogDefault
               || category == ToolCategory.Unknown
               || _categoryPolicy.GetValueOrDefault(category, defaultValue: false)
               || _toolOverrides.GetValueOrDefault(toolName, defaultValue: false);
    }

    /// <summary>
    ///     Builds a policy from the persisted node-default settings. Unknown category names are ignored (parsed
    ///     case-insensitively against <see cref="ToolCategory" />) and only entries that ADD approval
    ///     (<see langword="true" />) are retained, so the composed policy is purely a tighten-set — a stored
    ///     <see langword="false" /> can never loosen a tool. A <see langword="null" /> / empty settings object yields an
    ///     empty policy that is equivalent to <see cref="PermissiveToolApprovalPolicy" /> (identity on the catalog default).
    /// </summary>
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
