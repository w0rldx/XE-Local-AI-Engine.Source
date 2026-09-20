namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Runtime.InteropServices;

/// <summary>
///     Stable <see cref="DevelopmentAttemptEvidenceException.FailureCode" /> values, so a UI can localize them and an
///     operator can tell two different failures apart.
/// </summary>
/// <remarks>
///     The deterministic validation gate reports the same way (<see cref="DevelopmentValidationFailureCodes" />).
///     Without codes a self-inflicted attempt failure collapses into one sentence with no artifacts persisted,
///     because evidence is persisted only once the attempt passes its own checks — so the operator is told that
///     something went wrong and given nothing to act on, for a failure the engine had fully diagnosed.
/// </remarks>
internal static class DevelopmentAttemptFailureCodes
{
    /// <summary>The typed submission's changed-file list is not exactly the workspace's changed-file manifest.</summary>
    public const string ChangedFileManifestMismatch = "changed_file_manifest_mismatch";

    /// <summary>The typed submission claims a command id that produced no command evidence.</summary>
    public const string UnexecutedCommandClaimed = "unexecuted_command_claimed";

    /// <summary>The attempt ended without the typed submission its contract requires.</summary>
    public const string MissingSubmission = "missing_submission";

    /// <summary>The typed submission was recorded, but without the summary its contract requires.</summary>
    public const string MissingSummary = "missing_summary";

    /// <summary>The typed submission was offered more than once.</summary>
    public const string DuplicateSubmission = "duplicate_submission";

    /// <summary>The model asked for more tool calls than the attempt's budget allows.</summary>
    public const string ToolCallBudgetExceeded = "tool_call_budget_exceeded";

    /// <summary>The model produced more output than the attempt's whole-attempt output budget allows.</summary>
    public const string OutputTokenBudgetExceeded = "output_token_budget_exceeded";

    /// <summary>The provider returned no usable token accounting, so the attempt's budgets cannot be enforced.</summary>
    public const string UsageNotReported = "usage_not_reported";

    /// <summary>A workspace policy refused the attempt's own diff; the test-write policy is the one that fires in practice.</summary>
    /// <remarks>
    ///     The code exists so the workflow lane classes it as a policy refusal rather than a provider error, which is
    ///     what the retry budget is spent on.
    /// </remarks>
    public const string WorkspacePolicyRefused = "workspace_policy_refused";
}

/// <summary>
///     A Development attempt failure the engine diagnosed itself and can describe to the operator verbatim.
/// </summary>
/// <remarks>
///     The message is authored here, never assembled from model output or an absolute host path, which is what lets
///     the attempt runners surface it directly instead of replacing it with a generic sentence. Anything the engine
///     did <em>not</em> author still falls through to the generic reason; the sanitization rule is unchanged and
///     simply no longer applied to messages that were already safe.
/// </remarks>
internal sealed class DevelopmentAttemptEvidenceException : InvalidOperationException
{
    /// <summary>The width of <c>development_attempts.terminal_reason</c>.</summary>
    private const int MaxTerminalReasonLength = 1024;

    public DevelopmentAttemptEvidenceException(string failureCode, string operatorReason)
        : base(operatorReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorReason);
        FailureCode = failureCode;
        OperatorReason = operatorReason;
    }

    /// <summary>A stable <see cref="DevelopmentAttemptFailureCodes" /> value.</summary>
    public string FailureCode { get; }

    /// <summary>The operator-facing reason, unclamped.</summary>
    public string OperatorReason { get; }

    /// <summary>The value the attempt runners persist, composed and clamped here as one string.</summary>
    /// <remarks>
    ///     Clamping the reason alone and then prefixing the code re-introduces the overflow it prevents, and
    ///     <c>development_attempts.terminal_reason</c> is <c>HasMaxLength(1024)</c>.
    /// </remarks>
    public string TerminalReason => Compose(FailureCode, OperatorReason);

    /// <summary>
    ///     The same composition for a reason the engine authored without throwing — a policy refusal caught and turned
    ///     into a terminal reason rather than raised as one.
    /// </summary>
    /// <remarks>
    ///     One formatter, so the code prefix a reader and the workflow lane match on cannot drift between the paths.
    /// </remarks>
    public static string Compose(string failureCode, string operatorReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorReason);
        return Clamp($"[{failureCode}] {operatorReason}");
    }

    /// <summary>Whether <paramref name="terminalReason" /> is one this code composed for <paramref name="failureCode" />.</summary>
    public static bool Names(string? terminalReason, string failureCode) =>
        terminalReason?.StartsWith($"[{failureCode}]", StringComparison.Ordinal) == true;

    private static string Clamp(string reason) =>
        reason.Length <= MaxTerminalReasonLength ? reason : reason[..MaxTerminalReasonLength];
}

/// <summary>
///     The token accounting both attempt roles enforce, in one place because they enforced it differently by accident.
/// </summary>
/// <remarks>
///     <see cref="Microsoft.Extensions.AI.ChatOptions.MaxOutputTokens" /> is a per-provider-call ceiling the provider
///     enforces every round, while the usage from <c>ToChatResponse()</c> is the sum over every round of the tool
///     loop — which is why the input side multiplies by the round count sizing <c>MaxCumulativeInputTokens</c>.
///     Comparing the cumulative total against the per-call ceiling fails any attempt whose rounds together out-talk
///     one round's budget: a coder attempt on a 32768 per-call budget reported 33k+ cumulative tokens and lost it.
/// </remarks>
internal static class DevelopmentAttemptOutputBudget
{
    /// <summary>
    ///     The whole-attempt output ceiling for a per-call budget of <paramref name="maxOutputTokens" /> over
    ///     <paramref name="providerCalls" /> rounds. Mirrors how the input ceiling is derived, so the two budgets
    ///     describe the same shape of run.
    /// </summary>
    public static long Cumulative(int maxOutputTokens, int providerCalls) =>
        Math.Max(1L, (long)Math.Max(1, maxOutputTokens) * Math.Max(1, providerCalls));

    /// <summary>
    ///     Validates the usage a completed attempt reported, and returns the accepted (input, output) pair.
    ///     <paramref name="role" /> only names the failing role in the operator reason.
    /// </summary>
    public static AcceptedTokenUsage Accept(long? reportedInputTokens,
        long? reportedOutputTokens,
        long? reportedTotalTokens,
        int maxOutputTokens,
        int providerCalls,
        string role)
    {
        if (reportedInputTokens is not { } inputTokens || reportedOutputTokens is not { } outputTokens)
        {
            throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.UsageNotReported,
                $"The Development {role} model returned no token accounting, so the attempt's token budgets could not be enforced. "
                + "Select a model whose provider reports usage.");
        }

        var accountedTokens = checked(inputTokens + outputTokens);
        var totalTokens = Math.Max(reportedTotalTokens ?? accountedTokens, accountedTokens);
        var cumulativeCeiling = Cumulative(maxOutputTokens, providerCalls);
        if (inputTokens < 0 || outputTokens < 0 || totalTokens < 0)
        {
            throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.UsageNotReported,
                $"The Development {role} model reported negative token counts, so the attempt's token budgets could not be enforced.");
        }

        if (outputTokens > cumulativeCeiling)
        {
            throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.OutputTokenBudgetExceeded,
                $"The Development {role} produced {outputTokens} output tokens across the attempt, above the whole-attempt budget of "
                + $"{cumulativeCeiling} ({maxOutputTokens} per provider call over at most {providerCalls} calls). "
                + "Raise the project's maximum-tokens budget, or give the task a narrower objective so it needs fewer rounds.");
        }

        return new AcceptedTokenUsage(inputTokens, outputTokens);
    }
}

/// <summary>The token counts a completed attempt reported, after they passed the budget checks.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct AcceptedTokenUsage(long InputTokens, long OutputTokens);
