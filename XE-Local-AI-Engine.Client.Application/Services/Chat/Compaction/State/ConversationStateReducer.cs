namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

using System.Globalization;

/// <summary>
///     Applies a distiller delta to a state document: mints ids, links supersessions, retires entries and enforces the
///     entry and character budget. Pure; the model never rewrites the document itself.
/// </summary>
public static class ConversationStateReducer
{
    public const int MaxValueChars = 500;

    public static ConversationStateReduceResult Apply(ConversationStateDocument document, ConversationStateDelta delta,
        int atSequence, ConversationCompactionOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(options);
        if (delta.IsEmpty)
        {
            return new ConversationStateReduceResult { Document = document };
        }

        var entries = document.Entries.ToList();
        var rejections = new List<ConversationStateRejection>();
        // The persisted counter wins over the largest id present, so an id whose entry the budget dropped is never reminted.
        var nextId = Math.Max(document.NextEntryNumber, entries.Select(static entry => IdNumber(entry.Id)).DefaultIfEmpty(0).Max() + 1);

        foreach (var proposed in delta.Add)
        {
            var value = proposed.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                rejections.Add(new ConversationStateRejection { Operation = "add", Reason = "blank value" });
                continue;
            }

            var id = string.Create(CultureInfo.InvariantCulture, $"e{nextId}");
            nextId++;
            foreach (var supersededId in proposed.Supersedes)
            {
                var index = LiveIndex(entries, supersededId);
                if (index < 0)
                {
                    rejections.Add(new ConversationStateRejection { Operation = "add", EntryId = supersededId, Reason = "superseded id is unknown or not live" });
                    continue;
                }

                entries[index] = Copy(entries[index], supersededById: id, retiredAtSequence: null);
            }

            entries.Add(new ConversationStateEntry
            {
                Id = id,
                Category = proposed.Category,
                Value = ConversationSummarizer.TruncateAtRuneBoundary(value, MaxValueChars),
                SourceSequences = proposed.SourceSequences.Distinct().Order().ToList(),
                CreatedAtSequence = atSequence
            });
        }

        foreach (var retirement in delta.Supersede)
        {
            var index = LiveIndex(entries, retirement.EntryId);
            if (index < 0)
            {
                rejections.Add(new ConversationStateRejection { Operation = "supersede", EntryId = retirement.EntryId, Reason = "id is unknown or not live" });
                continue;
            }

            entries[index] = Copy(entries[index], supersededById: null, retiredAtSequence: retirement.BySequence);
        }

        foreach (var resolvedId in delta.Resolve)
        {
            var index = LiveIndex(entries, resolvedId);
            if (index < 0 || entries[index].Category != ConversationStateCategory.OpenQuestion)
            {
                rejections.Add(new ConversationStateRejection { Operation = "resolve", EntryId = resolvedId, Reason = "id is not a live open question" });
                continue;
            }

            entries[index] = Copy(entries[index], supersededById: null, retiredAtSequence: atSequence);
        }

        var dropped = EnforceBudget(entries, options);
        return new ConversationStateReduceResult
        {
            Document = new ConversationStateDocument { Version = document.Version, NextEntryNumber = (int)nextId, Entries = entries },
            Rejections = rejections,
            DroppedIds = dropped
        };
    }

    /// <summary>The numeric suffix of an <c>eN</c> id, or 0 for an id the reducer did not mint.</summary>
    internal static long IdNumber(string id)
    {
        return id.Length > 1 && id[0] == 'e' && long.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
    }

    // ponytail: fixed three-tier drop order (history, then routine facts, then intent) is the ceiling; upgrade path is
    // a category-aware re-distillation that merges entries instead of dropping them.
    private static List<string> EnforceBudget(List<ConversationStateEntry> entries, ConversationCompactionOptions options)
    {
        var totalChars = entries.Sum(static entry => entry.Value.Length);
        var victims = new HashSet<ConversationStateEntry>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in entries
                     .OrderBy(static entry => DropTier(entry))
                     .ThenBy(static entry => entry.CreatedAtSequence)
                     .ThenBy(static entry => IdNumber(entry.Id)))
        {
            if (entries.Count - victims.Count <= options.MaxStateEntries && totalChars <= options.MaxStateChars)
            {
                break;
            }

            victims.Add(candidate);
            totalChars -= candidate.Value.Length;
        }

        entries.RemoveAll(victims.Contains);
        return victims.Select(static entry => entry.Id).ToList();
    }

    private static int DropTier(ConversationStateEntry entry)
    {
        if (!entry.IsLive)
        {
            return 0;
        }

        return entry.Category is ConversationStateCategory.Fact or ConversationStateCategory.ToolOutcome or ConversationStateCategory.CompletedWork
            ? 1
            : 2;
    }

    private static int LiveIndex(List<ConversationStateEntry> entries, string? id)
    {
        return entries.FindIndex(entry => entry.IsLive && string.Equals(entry.Id, id, StringComparison.Ordinal));
    }

    private static ConversationStateEntry Copy(ConversationStateEntry entry, string? supersededById, int? retiredAtSequence)
    {
        return new ConversationStateEntry
        {
            Id = entry.Id,
            Category = entry.Category,
            Value = entry.Value,
            SourceSequences = entry.SourceSequences,
            SupersededById = supersededById ?? entry.SupersededById,
            RetiredAtSequence = retiredAtSequence ?? entry.RetiredAtSequence,
            CreatedAtSequence = entry.CreatedAtSequence
        };
    }
}
