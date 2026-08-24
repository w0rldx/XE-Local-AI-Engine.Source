namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
///     The one-per-model record that a graded reasoning effort's thinking budget was DROPPED because the resolved
///     model's chat template renders no reasoning end marker — the shape llama.cpp turns into the non-empty
///     think-end-tag set its <c>reasoning_budget_tokens</c> gate requires. Without that set the server accepts the
///     field and then ignores it, so emitting the budget would only claim a cap that does not exist; every marker
///     emitter therefore omits it and reports here instead.
///     <para>
///         The skip is a per-TURN decision on a hot path, so the notice is de-duplicated per model id: an operator
///         needs to learn once that this model's thinking cap does not apply, not once per send. Information level —
///         it explains an intentional, correct behavioral difference, not a fault.
///     </para>
/// </summary>
/// <remarks>
///     Process-lifetime state by necessity ("once" has to remember something). The key space is the set of model ids
///     this node actually runs, which is bounded by the installed model set, so the map cannot grow with traffic.
///     Because the memory is process-wide, a test that asserts the log line must use a model id no other test has
///     used — the same id in a second test would be correctly suppressed as a repeat.
/// </remarks>
internal static class ReasoningBudgetSkipLog
{
    private const string UnknownModelId = "(unknown)";

    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Reports, at most once per <paramref name="modelId" /> for the life of the process, that this model's graded
    ///     reasoning effort was applied WITHOUT a thinking budget. A blank/absent model id is folded onto one shared
    ///     key rather than skipped, so the notice is still emitted exactly once.
    /// </summary>
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
