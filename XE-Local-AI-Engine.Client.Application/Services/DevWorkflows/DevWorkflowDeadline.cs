namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     When a node run's current attempt runs out of time.
///     <para>
///         Derived from the ROW — its <c>StartedAtUtc</c> plus the node's declared timeout — and never held in memory. A
///         deadline a process owns dies with that process, and the node run it was bounding would then run until
///         something else noticed; re-deriving it from the row on every tick is what makes a restart cost nothing here.
///     </para>
///     <para>
///         A pause needs no special case, which is the payoff for deriving it this way. The store clears
///         <c>StartedAtUtc</c> whenever a row goes back to <c>Pending</c>, and a run only reaches <c>Paused</c> once
///         nothing under it is <c>Queued</c> or <c>Running</c> — so a paused run holds no deadline that could expire
///         while nobody is working, and the resume's re-admission stamps the fresh instant the next attempt counts from.
///         That is walkthrough #11, answered by the write order rather than by a clear-and-re-base pair that could be
///         forgotten on one of the paths into a pause.
///     </para>
/// </summary>
internal static class DevWorkflowDeadline
{
    /// <summary>
    ///     How long past its own deadline a node run is left alone before the dispatcher ends it itself.
    ///     <para>
    ///         The sandbox lane bounds its pass by the same node timeout counted from a moment EARLIER — the row is
    ///         written <c>Running</c> after the pass is started — and then needs a moment to sanitize its evidence and
    ///         compose a report. Ending the row the instant the number is reached would race that better answer and
    ///         sometimes win by milliseconds, throwing away the evidence for nothing. This is the backstop for a lane
    ///         that did NOT answer its own budget, so it is deliberately later than the budget it backs up.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Whether this node run has been running longer than its node allows, by enough that its own lane is not going
    ///     to answer for it.
    ///     <para>
    ///         Always false for a node that declares no timeout, deliberately. The defaults §8.2 names for the sandbox
    ///         node types are the DEVELOPMENT attempt budget, which the lane below already applies to the work it can
    ///         actually see — and a second number derived up here could only ever disagree with it. What this adds is
    ///         the bound nothing else has: an agent node run whose session never lands, and a sandbox pass that stops
    ///         answering its own budget.
    ///     </para>
    /// </summary>
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
