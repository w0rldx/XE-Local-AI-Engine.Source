namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     The test-write policy: the agent may ADD test files, but may not modify or delete one that already existed at
///     the attempt's base commit.
/// </summary>
/// <remarks>
///     This is the primary reward-hacking control: "delete the failing test" is a strictly shorter path to green than
///     "fix the bug", and nothing else in the pipeline distinguishes the two, because validation sees only that
///     everything passed. Permitting additions is what keeps "implement a feature and its tests" a legal task.
/// </remarks>
internal static class DevelopmentTestWritePolicy
{
    /// <summary>Change types that leave every pre-existing file intact.</summary>
    /// <remarks>
    ///     These are <see cref="DevelopmentPatchEvidenceService" />'s mapped words, never git's raw status letters:
    ///     comparing against the letters matches nothing, which silently inverts the policy into rejecting every
    ///     newly added test. <c>copied</c> belongs with <c>added</c> because git reports a copy only when the source
    ///     survives, so the protected original is untouched and only a new file appears.
    /// </remarks>
    private static readonly string[] NonDestructiveChangeTypes = ["added", "copied"];

    /// <summary>The refusal, in the words the operator is given.</summary>
    /// <remarks>
    ///     A constant because it is surfaced rather than replaced: the coder runner puts it on the attempt's terminal
    ///     reason, and the workflow lane's tests script it.
    /// </remarks>
    internal const string RefusalSentence =
        "The attempt modified or deleted a test that existed at the base commit, which the Development test-write policy does not permit. "
        + "Adding new test files is allowed.";

    /// <summary>The same rule stated BEFORE the fact, for the coder and reviewer prompts.</summary>
    /// <remarks>
    ///     <see cref="RefusalSentence" /> only ever reaches a round that has already lost its attempt to the rule, and
    ///     reaches the reviewer never, which deadlocks a task whose requirements demand an edit the policy forbids.
    ///     "renamed" is in the sentence because <see cref="Ensure" /> checks <c>PreviousPath</c> too: stating a rule
    ///     narrower than the one enforced is that same deadlock, one file class over.
    /// </remarks>
    internal const string PromptSentence =
        "Workspace test-write policy: a file that existed at the base commit and matches one of the protected test patterns "
        + "may not be modified, deleted or renamed; adding new files is allowed.";

    /// <summary>The patterns a prompt names before it counts the rest. The shipped profile has nine.</summary>
    private const int MaxPromptedPatterns = 20;

    /// <summary>The rule plus the profile's own globs.</summary>
    /// <remarks>
    ///     The rule is a path-glob set and not a notion of "test file": <c>tests/**/*.cs</c> protects fixtures,
    ///     harnesses and helpers as firmly as a test class, and a round told only the prose paraphrase spends its
    ///     whole attempt discovering that.
    /// </remarks>
    internal static string Prompt(DevelopmentCommandProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var shown = profile.ProtectedPaths.Order(StringComparer.Ordinal).Take(MaxPromptedPatterns).ToArray();
        if (shown.Length == 0)
        {
            return PromptSentence;
        }

        var remainder = profile.ProtectedPaths.Count - shown.Length;
        return $"{PromptSentence} Protected test patterns: {string.Join(", ", shown)}"
               + (remainder > 0 ? $" (+{remainder} more)." : ".");
    }

    /// <summary>
    ///     Throws when the attempt's diff modifies, deletes or renames a path matching the profile's protected test
    ///     patterns.
    /// </summary>
    /// <remarks>
    ///     The evidence comes from <c>git diff --cached --name-status -z HEAD</c>, and the managed worktree is
    ///     detached at the base commit with that invariant re-checked after every catalog command, so HEAD here is the
    ///     base commit the policy specifies. A rename is checked against its previous path as well as its new one,
    ///     because renaming a test out of the protected set removes coverage as effectively as deleting it, and an
    ///     unrecognized change type is treated as destructive rather than waved through.
    /// </remarks>
    public static void Ensure(DevelopmentPatchEvidence evidence, DevelopmentCommandProfile profile)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(profile);
        var offending = evidence.ChangedFiles
                                .Where(static change => !NonDestructiveChangeTypes.Contains(change.ChangeType, StringComparer.Ordinal))
                                .SelectMany(static change => new[]
                                {
                                    change.Path,
                                    change.PreviousPath
                                })
                                .Where(path => !string.IsNullOrWhiteSpace(path) && profile.IsProtectedTestPath(path))
                                .Distinct(StringComparer.Ordinal)
                                .ToArray();
        if (offending.Length > 0)
        {
            throw new DevelopmentWorkspaceSecurityException(RefusalSentence);
        }
    }
}
