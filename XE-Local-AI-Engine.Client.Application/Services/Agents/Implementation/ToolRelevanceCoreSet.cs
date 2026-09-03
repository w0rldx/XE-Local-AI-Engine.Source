namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

/// <summary>
///     The node's always-on tool names: the four work-session state tools, plus every approval-bearing BUILT-IN from
///     the node tool catalog. Lives here rather than in the agent assembly because both inputs do — the work-session
///     catalog is internal to this layer, and the <c>Source</c> tag that tells a built-in from an MCP or custom tool
///     exists only on <see cref="LocalToolCatalogEntry" />. The agent layer consumes nothing but the resulting names.
///     <para>
///         Deriving "built-in" from the catalog's own <c>Source</c> tag is also what keeps the hop free of
///         <c>mcp__</c> / <c>custom__</c> prefix heuristics. MCP and custom tools are deliberately absent: they are the
///         tools that push a real agent past the threshold, and hiding one changes nothing about calling one — its
///         approval wrap is applied at registry build and is never unwrapped.
///     </para>
///     <para>
///         <b>Which approval flag this reads.</b> <see cref="LocalToolCatalogEntry.RequiresApproval" /> is the STATIC
///         catalog default, not the effective flag the tighten-only node policy composes at resolution. That is
///         deliberate: the core set is then a fixed, deterministic set of names for the node, identical across agents
///         and turns, so tightening an approval policy changes how a tool is CALLED and never which tools the model is
///         SHOWN. The consequence to accept is that a built-in the node policy has tightened is ranked like any other
///         non-core tool and can be hidden — which costs nothing, because being hidden never bypasses the approval it
///         just gained.
///     </para>
/// </summary>
internal sealed class ToolRelevanceCoreSet : IToolRelevanceCoreSet
{
    private const string BuiltinSource = "builtin";

    private readonly ILocalToolOfferProvider _offerProvider;

    public ToolRelevanceCoreSet(ILocalToolOfferProvider offerProvider)
    {
        _offerProvider = offerProvider ?? throw new ArgumentNullException(nameof(offerProvider));
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetCoreToolNames()
    {
        // Read live rather than cached: the MCP registry (and therefore the catalog) is populated as servers connect,
        // and the composition below is a handful of string comparisons over a precomputed built-in list.
        var core = new HashSet<string>(WorkSessionToolDefinitions.ToolNames, StringComparer.Ordinal);

        foreach (var entry in _offerProvider.GetKnownTools())
        {
            if (entry.RequiresApproval && string.Equals(entry.Source, BuiltinSource, StringComparison.Ordinal))
            {
                _ = core.Add(entry.Name);
            }
        }

        return core;
    }
}
