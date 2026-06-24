namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Folds an agent's enabled playbook actions into its system prompt. The header text and bullet format live here
///     alone so the deferred relevance-retrieval path can reuse the composer with a filtered subset.
///     Scope-aware (adaptive memory): positive guidance (Procedural / UserPreference / Project, plus untyped
///     legacy actions) is rendered in the "Operating Playbook" section, while <see cref="MemoryScope.Failure" /> items are
///     rendered in a SEPARATE, tightly-framed negative-guidance section ("avoid / what NOT to do") so the model does not
///     read failures as instructions to follow.
/// </summary>
internal static class PlaybookPromptComposer
{
    private const string Header = "\n\n## Operating Playbook\n";

    // Failure-scope items render here, framed tightly so the model treats them as things to avoid, never as steps to
    // perform — negative guidance can backfire if it reads like an instruction, so it is fenced off and concise.
    private const string FailureHeader = "\n\n## Avoid (lessons from past failures)\nDo NOT repeat these mistakes:\n";

    /// <summary>
    ///     Returns <paramref name="baseInstructions" /> with the enabled actions appended as labeled bullet lists, in the
    ///     order supplied (the store/selector already orders by Priority then CreatedAtUtc; the composer never re-sorts the
    ///     positive section). <see cref="MemoryScope.Failure" /> items are pulled into a separate negative-guidance section,
    ///     emitted AFTER the positive section and re-ordered DETERMINISTICALLY (Priority ascending, then CreatedAtUtc
    ///     ascending) so the composed text — and thus the runtime config hash — is stable for a fixed memory set across
    ///     sends (resume-safety). An empty list — OR a list with no Failure items and whose positive items match the legacy
    ///     shape — composes byte-identically to the pre-scope path: an empty list returns <paramref name="baseInstructions" />
    ///     <b>verbatim</b> (no header, no trailing delimiter), and an untyped-only/Failure-free list emits ONLY the
    ///     "Operating Playbook" section exactly as before. That byte-identical guarantee is the central regression invariant.
    /// </summary>
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
