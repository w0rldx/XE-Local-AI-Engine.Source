namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
///     The one-per-model record that a graded reasoning effort's thinking budget was DROPPED because the resolved
///     model's chat template renders no reasoning end marker.
/// </summary>
/// <remarks>
///     That marker is the shape llama.cpp turns into the non-empty think-end-tag set its <c>reasoning_budget_tokens</c>
///     gate requires; without it the server accepts the field and ignores it, so every marker emitter omits the budget
///     and reports here at Information level — an intentional difference, not a fault. De-duplicated per model id,
///     because an operator needs to learn this once, not once per send. Process-lifetime state by necessity, bounded by
///     the installed model set; a test asserting the log line must use a model id no other test has used.
/// </remarks>
internal static class ReasoningBudgetSkipLog
{
    private const string UnknownModelId = "(unknown)";

    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Reports, at most once per <paramref name="modelId" /> for the life of the process, that this model's graded
    ///     reasoning effort was applied WITHOUT a thinking budget.
    /// </summary>
    /// <remarks>
    ///     A blank or absent model id is folded onto one shared key rather than skipped, so the notice is still emitted
    ///     exactly once.
    /// </remarks>
    internal static void ReportBudgetSkipped(ILogger logger, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var key = string.IsNullOrWhiteSpace(modelId) ? UnknownModelId : modelId.Trim();
        if (!Reported.TryAdd(key, value: 0))
        {
            return;
        }

        logger.LogInformation(
            "Model {ModelId} advertises graded reasoning, but its chat template renders no reasoning end marker, so llama.cpp cannot enforce a per-request thinking budget for it. The reasoning effort still applies; the token cap is omitted rather than sent and silently ignored.",
            key);
    }
}
