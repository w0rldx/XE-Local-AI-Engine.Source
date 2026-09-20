namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Globalization;
using System.Text;

/// <summary>
///     The one rendering of the rule-set bodies a node run RECORDED into the text it is dispatched with.
/// </summary>
/// <remarks>
///     Two lanes inject policy and must not drift: the agent lane writes these sections into its objective, the
///     DevTask lane writes them onto the Development task as an event the prompts read back. Same fair share, same
///     visible truncation marker, same dropped-with-a-warning; only the ceiling differs, which is the caller's to
///     name. Every entry comes from the node run's own snapshot, never the rule set as it stands now — editing one
///     mid-run is allowed, so a dispatch-time read would hand the node a document the audit does not name.
/// </remarks>
internal static class DevWorkflowPolicyText
{
    /// <summary>
    ///     Room set aside from a section's share for the truncation marker that may follow its body.
    /// </summary>
    /// <remarks>
    ///     Comfortably over the marker's worst case. The append guard is what enforces the bound, so this only has to
    ///     stop the common case from being dropped for the sake of a few characters. Shared with the agent lane's
    ///     upstream-artifact phase, which splits its budget the same way.
    /// </remarks>
    internal const int TruncationMarkerReserve = 64;

    /// <summary>
    ///     The <c>## Policy: name</c> sections, in the order they were recorded, fitted into what is left of
    ///     <paramref name="ceiling" /> once <paramref name="occupied" /> characters are already spent. Empty when
    ///     nothing applied or nothing fit.
    /// </summary>
    /// <param name="applied">What the node run recorded, bodies and all.</param>
    /// <param name="ceiling">The total the composed text must not exceed, in characters.</param>
    /// <param name="occupied">How much of that ceiling the caller has already spent before this text is appended.</param>
    /// <param name="nodeRunId">Named in every warning, because a dropped policy is an audit question.</param>
    /// <param name="logger">Where a skipped, truncated or dropped section is reported.</param>
    internal static string Render(IReadOnlyList<DevWorkflowAppliedRuleSet> applied, int ceiling, int occupied, Guid nodeRunId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(logger);

        var rendered = new StringBuilder();
        for (var index = 0; index < applied.Count; index++)
        {
            var entry = applied[index];

            // Rows written before the body was snapshotted alongside the hash. The reader stays honest about them rather than re-reading the rule set and injecting whatever it says
            // NOW, which is the exact divergence the snapshot exists to close.
            if (entry.Body is not { Length: > 0 })
            {
                logger.LogWarning("Development workflow node run {NodeRunId} recorded rule set {RuleSetId} without its text, so nothing was injected for it. "
                                  + "The recorded content hash {RecordedHash} still names what applied.",
                    nodeRunId,
                    entry.Id,
                    entry.ContentSha256);
                continue;
            }

            // The name is the one RECORDED at materialization, not the one the row carries now: it is what the audit
            // says applied, and rendering a since-renamed heading would put a second story in the text.
            var header = string.Create(CultureInfo.InvariantCulture, $"{Environment.NewLine}## Policy: {entry.Name}{Environment.NewLine}");

            // The same fair share the agent lane's upstream artifacts get: everything still to be written splits the room left equally, so a long first policy cannot crowd out the
            // ones after it and a short one hands its slack on.
            var written = occupied + rendered.Length;
            var share = (ceiling - written) / (applied.Count - index);
            var budget = share - header.Length - TruncationMarkerReserve;
            if (budget <= 0)
            {
                logger.LogWarning("Development workflow rule set {RuleSetId} was dropped from node run {NodeRunId}'s policy text: the {Limit}-character "
                                  + "budget was already spent before its section could be written.",
                    entry.Id,
                    nodeRunId,
                    ceiling);
                continue;
            }

            var body = entry.Body;
            if (body.Length > budget)
            {
                // Truncated rather than dropped, and VISIBLY: a model handed the first half of a policy has to be able
                // to tell that the rest exists, or it will act as though the rules it was given were all of them.
                var cut = CutAt(body, budget);
                logger.LogWarning("Development workflow rule set {RuleSetId} was truncated in node run {NodeRunId}'s policy text: {Kept} of {Total} characters.",
                    entry.Id,
                    nodeRunId,
                    cut,
                    body.Length);
                body = string.Create(CultureInfo.InvariantCulture, $"{body[..cut]}{Environment.NewLine}[policy text truncated: {cut} of {body.Length} characters]");
            }

            // Enforced HERE, on the FINISHED block, exactly as the agent lane's artifact phase does it: nothing reaches
            // the text except through this check, so no marker or rounding can push it past the limit.
            var policy = header + body + Environment.NewLine;
            if (written + policy.Length <= ceiling)
            {
                _ = rendered.Append(policy);
            }
            else
            {
                logger.LogWarning("Development workflow rule set {RuleSetId} was dropped from node run {NodeRunId}'s policy text: its section did not fit the "
                                  + "{Limit}-character budget.",
                    entry.Id,
                    nodeRunId,
                    ceiling);
            }
        }

        return rendered.ToString();
    }

    /// <summary>
    ///     Where to cut <paramref name="text" /> to fit <paramref name="budget" /> UTF-16 code units.
    /// </summary>
    /// <remarks>
    ///     Never BETWEEN a surrogate pair. The budget counts code units and an astral character — an emoji, most CJK
    ///     extensions — is two of them, so keeping only the high half leaves an unpaired surrogate that becomes
    ///     U+FFFD once the text is persisted as UTF-8: the model would be handed a corrupted character, not one fewer.
    /// </remarks>
    internal static int CutAt(string text, int budget) =>
        char.IsHighSurrogate(text[budget - 1]) ? budget - 1 : budget;
}
