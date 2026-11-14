namespace XE_Local_AI_Engine.Client.Services.Knowledge.Tools;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Applies the turn's hard-picked knowledge collection (<see cref="TurnKnowledgeSourceCollector.PinnedCollectionId" />)
///     to the three knowledge-base tools, so a fixed chat cannot read any other collection.
/// </summary>
/// <remarks>
///     A call that names no collection gets the fixed one; a call naming another is refused rather than silently redirected,
///     so the model never presents one collection's passages as another's. Without a pin both are no-ops.
/// </remarks>
internal static class KnowledgeToolCollectionPin
{
    /// <summary>The collection the call names, or the fixed one when it names none; normalise the result as before.</summary>
    public static string? Requested(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? TurnKnowledgeSourceCollector.PinnedCollectionId ?? requested : requested;

    /// <summary>The refusal for a normalised collection other than the fixed one; <see langword="null" /> when the call may run.</summary>
    /// <remarks>In the <see cref="ToolFailureText" /> shape, so the tool result is recorded as an error, not a successful read.</remarks>
    public static string? Refusal(string toolName, string collectionId)
    {
        var pinned = TurnKnowledgeSourceCollector.PinnedCollectionId;
        return pinned is null || string.Equals(pinned, collectionId, StringComparison.Ordinal)
            ? null
            : ToolFailureText.Format(ToolFailureText.ToolReportedCode,
                $"{toolName} can only read the knowledge collection '{pinned}': the operator fixed this chat to it. Omit collectionId or pass '{pinned}'.");
    }
}
