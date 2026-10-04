namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     The window-fitted tool offer (model-matrix F5): the estimated tokens the ranked tools may use beside the pinned
///     ones, and what each rankable tool costs, by name.
/// </summary>
/// <remarks>
///     The relevance hop offers the pinned tools, then ranked tools in rank order while they fit the budget; a tool that
///     does not fit is skipped and the next one tried. A tool without a cost is never offered (<c>list_tools</c> can
///     still reveal it).
/// </remarks>
public sealed class FittedToolOffer
{
    public required int RankedTokenBudget { get; init; }

    public required IReadOnlyDictionary<string, int> TokenCosts { get; init; }
}
