namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     The test-write policy: the agent may ADD test files, but may not modify or delete one that already existed at
///     the attempt's base commit.
///     <para>
///         This is the primary reward-hacking control, and it is more load-bearing than the profile-file guard.
///         "Delete the failing test" is a strictly shorter path to green than "fix the bug", and nothing else in the
///         pipeline distinguishes the two — validation only sees that everything passed. Permitting additions is what
///         keeps "implement a feature and its tests" a legal task.
///     </para>
/// </summary>
internal static class DevelopmentTestWritePolicy
{
    /// <summary>
    ///     Change types that leave every pre-existing file intact.
    ///     <para>
    ///         These are <see cref="DevelopmentPatchEvidenceService" />'s mapped words, NOT git's raw status letters.
    ///         Comparing against <c>"A"</c>/<c>"M"</c>/<c>"D"</c> here matches nothing, which silently inverts the
    ///         policy into rejecting every newly added test — the precise opposite of what the test-write policy requires.
    ///     </para>
    ///     <para>
    ///         <c>copied</c> belongs here with <c>added</c>: git reports a copy only when the source survives, so the
    ///         protected original is untouched and only a new file appears.
    ///     </para>
    /// </summary>
    private static readonly string[] NonDestructiveChangeTypes = ["added", "copied"];

    /// <summary>
    ///     The refusal, in the words the operator is given. A constant because it is now surfaced rather than replaced:
    ///     the coder runner puts it on the attempt's terminal reason, and the workflow lane's tests script it.
    /// </summary>
    internal const string RefusalSentence =
        "The attempt modified or deleted a test that existed at the base commit, which the Development test-write policy does not permit. "
        + "Adding new test files is allowed.";

    /// <summary>
    ///     The same rule stated BEFORE the fact, for the coder and reviewer prompts. <see cref="RefusalSentence" />
    ///     only ever reaches a round that has already lost its attempt to the rule, and it reaches the reviewer never —
    ///     which live on 2026-09-04 deadlocked a task whose requirements demanded an edit the policy forbids: the
    ///     reviewer kept asking for it, and every coder round that obeyed was refused.
    ///     <para>
    ///         "renamed" is in the sentence because <see cref="Ensure" /> checks <c>PreviousPath</c> too. Stating a rule
    ///         narrower than the one enforced is the exact shape of the deadlock this fixes, one file class over.
    ///     </para>
    /// </summary>
    internal const string PromptSentence =
        "Workspace test-write policy: a file that existed at the base commit and matches one of the protected test patterns "
        + "may not be modified, deleted or renamed; adding new files is allowed.";

    /// <summary>The patterns a prompt names before it counts the rest. The shipped profile has nine.</summary>
    private const int MaxPromptedPatterns = 20;

    /// <summary>
    ///     The rule plus the profile's OWN globs, because the rule is a path-glob set and not a notion of "test file":
    ///     <c>tests/**/*.cs</c> protects fixtures, harnesses and helpers as firmly as it protects a test class, and a
    ///     round told only the prose paraphrase spends its whole attempt discovering that.
    /// </summary>
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
    ///     <para>
    ///         The evidence comes from <c>git diff --cached --name-status -z HEAD</c>, and the managed worktree is
    ///         detached at the base commit with that invariant re-checked after every catalog command — so HEAD here IS
    ///         the base commit, which is the comparison the test-write policy specifies.
    ///     </para>
    ///     <para>
    ///         A rename is checked against its previous path as well as its new one, because renaming a test out of the
    ///         protected set removes coverage just as effectively as deleting it. An unrecognized change type is
    ///         treated as destructive rather than waved through.
    ///     </para>
    /// </summary>
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
