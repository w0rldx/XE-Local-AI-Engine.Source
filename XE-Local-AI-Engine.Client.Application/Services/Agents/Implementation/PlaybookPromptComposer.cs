namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Folds an agent's enabled playbook actions into its system prompt.
/// </summary>
/// <remarks>
///     The header text and bullet format live here alone, so the relevance-retrieval path reuses the composer with a
///     filtered subset. Scope-aware: positive guidance renders in the "Operating Playbook" section while
///     <see cref="MemoryScope.Failure" /> items render in a SEPARATE, tightly-framed negative-guidance section, so
///     the model does not read failures as instructions to follow.
/// </remarks>
internal static class PlaybookPromptComposer
{
    private const string Header = "\n\n## Operating Playbook\n";

    // Failure-scope items render here, framed tightly so the model treats them as things to avoid, never as steps to
    // perform — negative guidance can backfire if it reads like an instruction, so it is fenced off and concise.
    private const string FailureHeader = "\n\n## Avoid (lessons from past failures)\nDo NOT repeat these mistakes:\n";

    /// <summary>
    ///     Returns <paramref name="baseInstructions" /> with the enabled actions appended as labeled bullet lists, in
    ///     the order supplied.
    /// </summary>
    /// <remarks>
    ///     The store and selector already order by Priority then CreatedAtUtc and the composer never re-sorts the
    ///     positive section. <see cref="MemoryScope.Failure" /> items are pulled into their own section AFTER it,
    ///     re-ordered deterministically so the text, and the config hash, stay stable for a fixed memory set. An
    ///     empty list returns <paramref name="baseInstructions" /> VERBATIM, and a Failure-free one emits only the
    ///     "Operating Playbook" section: that byte-identical guarantee is the central regression invariant.
    /// </remarks>
    public static string Compose(string baseInstructions, IReadOnlyList<PlaybookActionRecord> enabledOrderedByPriority)
    {
        ArgumentNullException.ThrowIfNull(enabledOrderedByPriority);

        if (enabledOrderedByPriority.Count == 0)
        {
            return baseInstructions;
        }

        // Partition without re-sorting the positive items: they arrive already ordered by the selector and the composer's
        // store-order contract forbids re-sorting them. Failure items ARE re-ordered deterministically below.
        var positive = new List<PlaybookActionRecord>(enabledOrderedByPriority.Count);
        var failures = new List<PlaybookActionRecord>();
        foreach (var action in enabledOrderedByPriority)
        {
            if (action.MemoryScope == MemoryScope.Failure)
            {
                failures.Add(action);
            }
            else
            {
                positive.Add(action);
            }
        }

        var composed = baseInstructions;

        if (positive.Count > 0)
        {
            var positiveBullets = string.Join("\n", positive.Select(static action => $"- {action.Behavior}"));
            composed += Header + positiveBullets;
        }

        if (failures.Count > 0)
        {
            // Deterministic Failure ordering (Priority asc, then CreatedAtUtc asc): the negative section is independent of
            // the selector's relevance order, so without this the prompt text would churn per send and break resume.
            var failureBullets = string.Join("\n",
                failures
                    .OrderBy(static action => action.Priority)
                    .ThenBy(static action => action.CreatedAtUtc)
                    .Select(static action => $"- {action.Behavior}"));
            composed += FailureHeader + failureBullets;
        }

        return composed;
    }
}
