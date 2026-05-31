namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Folds an agent's enabled playbook actions into its system prompt. The header text and bullet format live here
///     alone so the deferred P5 relevance-retrieval path can reuse the composer with a filtered subset.
/// </summary>
internal static class PlaybookPromptComposer
{
    private const string Header = "\n\n## Operating Playbook\n";

    /// <summary>
    ///     Returns <paramref name="baseInstructions" /> with the enabled actions appended as a labeled bullet list, in
    ///     the order supplied (the store already orders by Priority then CreatedAtUtc; the composer never re-sorts).
    ///     An empty list returns <paramref name="baseInstructions" /> <b>verbatim</b> — no header, no trailing delimiter
    ///     — so the resolved prompt stays byte-identical to the no-playbook path and the runtime config hash is
    ///     unchanged. That byte-identical guarantee is the central regression invariant.
    /// </summary>
    public static string Compose(string baseInstructions, IReadOnlyList<PlaybookActionRecord> enabledOrderedByPriority)
    {
        ArgumentNullException.ThrowIfNull(enabledOrderedByPriority);

        if (enabledOrderedByPriority.Count == 0)
        {
            return baseInstructions;
        }

        var bullets = string.Join("\n", enabledOrderedByPriority.Select(action => $"- {action.Behavior}"));
        return baseInstructions + Header + bullets;
    }
}
