namespace XE_Local_AI_Engine.Client.Services.Eval;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Computes a stable fingerprint over every behaviour-affecting input of a playbook eval run, so a recorded pass
///     authorizes promotion only while those inputs are unchanged.
/// </summary>
/// <remarks>
///     The action's own <c>Version</c> covers edits to the action; this extends staleness detection to the
///     SURROUNDING context — base instructions, sibling enabled actions, golden set, and the eval model's configured
///     NAME and resolved weight IDENTITY. Folding the identity closes the same-name-swap trap: a model re-pulled
///     under the SAME name moves the token, forcing a re-eval. It hashes only ids, versions, timestamps and an
///     instructions hash, never raw content, so it is safe beside the plaintext eval-result JSON.
/// </remarks>
public static class PlaybookEvalFingerprint
{
    /// <summary>
    ///     Bump when the eval harness, rubric, or scoring logic changes so an eval recorded by an older build stops
    ///     authorizing promotion (its fingerprint no longer matches the current evaluator's).
    /// </summary>
    public const string EvaluatorVersion = "v1";

    /// <summary>
    ///     Computes the fingerprint, over deterministically ordered inputs.
    /// </summary>
    /// <param name="enabledGoldenCases">The FULL enabled golden set, before the per-run cap.</param>
    /// <param name="modelName">The configured eval model NAME, so a re-point to another name moves the value.</param>
    /// <param name="modelIdentity">
    ///     The resolved weight IDENTITY token, or its explicit <c>"unverified"</c> sentinel.
    /// </param>
    /// <remarks>
    ///     Raising the per-run cap therefore does not move the fingerprint but editing the golden set does. The
    ///     sentinel shares no prefix with a verified token, so an unverifiable run never collides with a verified one.
    /// </remarks>
    public static string Compute(Guid suggestedActionId,
        int suggestedActionVersion,
        string agentInstructions,
        IReadOnlyList<PlaybookActionRecord> enabledActions,
        IReadOnlyList<GoldenConversationRecord> enabledGoldenCases,
        string modelName,
        string modelIdentity)
    {
        ArgumentNullException.ThrowIfNull(enabledActions);
        ArgumentNullException.ThrowIfNull(enabledGoldenCases);

        var builder = new StringBuilder();
        builder.Append("evaluator=").Append(EvaluatorVersion).Append('\n');
        builder.Append("model=").Append(modelName ?? string.Empty).Append('\n');
        builder.Append("modelIdentity=").Append(modelIdentity ?? string.Empty).Append('\n');
        builder.Append("suggested=").Append(suggestedActionId.ToString("N")).Append(':').Append(suggestedActionVersion).Append('\n');
        builder.Append("instructions=").Append(HashText(agentInstructions)).Append('\n');

        builder.Append("actions=");
        foreach (var action in enabledActions
                               .OrderBy(static action => action.Priority)
                               .ThenBy(static action => action.CreatedAtUtc)
                               .ThenBy(static action => action.Id))
        {
            builder.Append(action.Id.ToString("N")).Append(':').Append(action.Version).Append(';');
        }

        builder.Append('\n').Append("golden=");
        foreach (var golden in enabledGoldenCases.OrderBy(static golden => golden.Id))
        {
            // UpdatedAtUtc bumps whenever a golden case's inputs or expected outcomes (assertion/rubric) change, so
            // (id, updatedAt) captures an edit without decrypting the case's sensitive content.
            builder.Append(golden.Id.ToString("N")).Append(':').Append(golden.UpdatedAtUtc).Append(';');
        }

        builder.Append('\n');
        return HashText(builder.ToString());
    }

    private static string HashText(string? value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }
}
