namespace XE_Local_AI_Engine.Client.Services.Eval;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Computes a stable fingerprint over every behaviour-affecting input of a playbook eval run, so a recorded pass can
///     only authorize promotion while those inputs are unchanged. The action's own <c>Version</c> (checked separately by
///     the promote gate) covers edits to the action itself; this fingerprint extends staleness detection to the
///     SURROUNDING context an eval also depends on — the agent's base instructions, the sibling enabled actions, the
///     golden set, and the eval model (both its configured NAME and its resolved weight IDENTITY) — so a promote is
///     blocked when any of them changed after the eval ran, even though the action's version did not. Folding the model
///     identity alongside the name closes the same-name-swap trap: a model updated under the SAME name (an Ollama /
///     llama.cpp weight swap) leaves the name unchanged but changes the identity token, so the fingerprint no longer
///     matches and a re-eval is forced before the recorded pass can be trusted against the new weights. It hashes
///     ids/versions/timestamps (and a hash of the base instructions), never raw sensitive content, so it is safe to
///     persist alongside the plaintext eval-result JSON.
/// </summary>
public static class PlaybookEvalFingerprint
{
    /// <summary>
    ///     Bump when the eval harness, rubric, or scoring logic changes so an eval recorded by an older build stops
    ///     authorizing promotion (its fingerprint no longer matches the current evaluator's).
    /// </summary>
    public const string EvaluatorVersion = "v1";

    /// <summary>
    ///     Computes the fingerprint. <paramref name="enabledGoldenCases" /> is the FULL enabled golden set (before any
    ///     per-run <c>MaxGoldenCases</c> cap), so raising the cap does not move the fingerprint but adding/removing/editing
    ///     a golden case does. Inputs are ordered deterministically before hashing.
    /// </summary>
    /// <param name="modelName">The configured eval model NAME. Kept alongside the identity so a re-point to a different name still moves the fingerprint.</param>
    /// <param name="modelIdentity">
    ///     The resolved weight IDENTITY token for the eval model (from <c>IEvalModelIdentityResolver</c>): a content
    ///     hash / revision+size+download-time for a llama.cpp GGUF, an Ollama content digest, or the explicit
    ///     <c>"unverified"</c> sentinel when no identity source could be resolved. A same-name weight swap changes this
    ///     token (so the fingerprint no longer matches), and an unverifiable run yields the sentinel — distinct from any
    ///     verified token — so it can never collide with a verified run of the same name.
    /// </param>
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
