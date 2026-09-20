namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>When a node run's current attempt runs out of time.</summary>
/// <remarks>
///     Derived from the ROW — <c>StartedAtUtc</c> plus the node's declared timeout — never held in memory, because a
///     deadline a process owns dies with it and the node run would run on unnoticed. Restart recovery is NOT this
///     class's story: the reconciler collapses every in-flight sandbox row to <c>Pending</c> first, so such a row has
///     no start instant to expire. A pause needs no special case either — the store clears <c>StartedAtUtc</c> on the
///     way back to <c>Pending</c>, so the resume's re-admission stamps the instant the next attempt counts from.
/// </remarks>
internal static class DevWorkflowDeadline
{
    /// <summary>How long past its own deadline a node run is left alone before the dispatcher ends it itself.</summary>
    /// <remarks>
    ///     The sandbox lane bounds its pass by the same node timeout counted from a moment EARLIER — the row is
    ///     written <c>Running</c> after the pass starts — and then needs a moment to sanitize its evidence and compose
    ///     a report. Ending the row the instant the number is reached would race that better answer and sometimes win
    ///     by milliseconds, throwing the evidence away. This is the backstop for a lane that did NOT answer its own
    ///     budget, so it is deliberately later than the budget it backs up.
    /// </remarks>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Whether this node run has been running longer than its node allows, by enough that its own lane is not
    ///     going to answer for it.
    /// </summary>
    /// <remarks>
    ///     Always false for a node that declares no timeout, deliberately: the sandbox defaults are the DEVELOPMENT
    ///     attempt budget, which the lane below applies to the work it can actually see, and a second number derived
    ///     up here could only disagree with it. What this adds is the bound nothing else has — an agent node run whose
    ///     session never lands, and a sandbox pass that stops answering its own budget.
    /// </remarks>
    public static bool HasExpired(DevWorkflowGraphNode node, DevWorkflowNodeRunSnapshot nodeRun, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return Expiry(node, nodeRun) is { } expiry && timeProvider.GetUtcNow() >= expiry + Grace;
    }

    /// <summary>The absolute instant this attempt is due, or null when the node declares no timeout or has not started.</summary>
    public static DateTimeOffset? Expiry(DevWorkflowGraphNode node, DevWorkflowNodeRunSnapshot nodeRun)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);
        return node.NodeTimeoutSeconds is > 0 && nodeRun.StartedAtUtc is { } startedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(startedAt).AddSeconds(node.NodeTimeoutSeconds.Value)
            : null;
    }
}
