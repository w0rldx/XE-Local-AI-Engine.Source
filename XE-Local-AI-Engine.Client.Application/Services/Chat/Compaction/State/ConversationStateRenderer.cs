namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

using System.Text;

/// <summary>Renders the live entries of a state document as the plain-text block injected into a turn's context.</summary>
public static class ConversationStateRenderer
{
    private static readonly (ConversationStateCategory Category, string Heading)[] Groups =
    [
        (ConversationStateCategory.Goal, "Goals"),
        (ConversationStateCategory.Correction, "Corrections"),
        (ConversationStateCategory.OpenQuestion, "Open questions"),
        (ConversationStateCategory.Decision, "Decisions"),
        (ConversationStateCategory.Constraint, "Constraints"),
        (ConversationStateCategory.Fact, "Facts"),
        (ConversationStateCategory.ToolOutcome, "Tool outcomes"),
        (ConversationStateCategory.CompletedWork, "Completed work")
    ];

    /// <summary>Returns the rendered block, or null when the document has no live entry.</summary>
    /// <param name="asOfSequence">Renders the state as it stood at that anchor: later-sourced entries omitted, entries a later one replaced live again.</param>
    public static string? RenderForContext(ConversationStateDocument document, int? asOfSequence = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var byId = document.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (var (category, heading) in Groups)
        {
            var entries = document.Entries
                                  .Where(entry => entry.Category == category && (asOfSequence is { } asOf ? IsLiveAsOf(entry, asOf, byId) : entry.IsLive))
                                  .OrderBy(static entry => ConversationStateReducer.IdNumber(entry.Id))
                                  .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
                                  .ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(heading).Append(":\n");
            foreach (var entry in entries)
            {
                // One line per entry: a value's own line breaks would otherwise let it forge a fake entry line.
                builder.Append("- [").Append(entry.Id).Append("] ").Append(ToSingleLine(entry.Value)).Append('\n');
            }
        }

        return builder.Length == 0 ? null : builder.ToString().TrimEnd('\n');
    }

    private static bool IsLiveAsOf(ConversationStateEntry entry, int asOf, Dictionary<string, ConversationStateEntry> byId)
    {
        if (OriginSequence(entry) > asOf || (entry.RetiredAtSequence is { } retired && retired <= asOf))
        {
            return false;
        }

        // An unknown superseder (dropped by the budget) counts as before the cutoff: the entry stays superseded.
        return entry.SupersededById is null || (byId.TryGetValue(entry.SupersededById, out var superseder) && OriginSequence(superseder) > asOf);
    }

    // The sources place an entry more precisely than the call watermark it was minted at, which may span the cutoff.
    private static int OriginSequence(ConversationStateEntry entry) =>
        entry.SourceSequences.Count > 0 ? entry.SourceSequences.Max() : entry.CreatedAtSequence;

    private static string ToSingleLine(string value)
    {
        return value.ReplaceLineEndings(" ");
    }
}
