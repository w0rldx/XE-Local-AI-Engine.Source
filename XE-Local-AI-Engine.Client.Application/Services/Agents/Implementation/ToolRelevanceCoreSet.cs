namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

/// <summary>
///     The node's always-on tool names: the four work-session state tools, plus every approval-bearing BUILT-IN from
///     the node tool catalog.
/// </summary>
/// <remarks>
///     It lives here because both inputs do, and deriving "built-in" from the catalog's own <c>Source</c> tag keeps
///     the hop free of prefix heuristics. MCP and custom tools are deliberately absent: they push a real agent past
///     the threshold, and hiding one changes nothing about calling it, its approval wrap being applied at registry
///     build. The flag read is <see cref="LocalToolCatalogEntry.RequiresApproval" />, the STATIC catalog default and
///     not the composed one, so tightening a policy changes how a tool is CALLED, never which tools are SHOWN.
/// </remarks>
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
