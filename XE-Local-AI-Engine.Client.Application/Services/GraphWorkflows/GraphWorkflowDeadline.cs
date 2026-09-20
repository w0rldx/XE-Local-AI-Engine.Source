namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>When a node run's current attempt runs out of time.</summary>
/// <remarks>
///     Derived from the ROW — its <c>StartedAtUtc</c> plus the node's declared timeout — and never held in memory: a
///     deadline a process owns dies with that process, and the node run it bounded would run until something else
///     noticed. Unlike the development-workflow original, a node that declares no timeout still HAS one, from the
///     node-timeout default in options, which an author's <c>timeoutSeconds</c> only overrides. A row that never
///     started has no deadline at all, which is what leaves a re-attempt and a restart collapse nothing to expire.
/// </remarks>
internal static class GraphWorkflowDeadline
{
    /// <summary>How long past its own deadline a node run is left alone before the run ends it itself.</summary>
    /// <remarks>
    ///     The agent lane bounds its own turn by the SAME number counted from a moment slightly later — the runner
    ///     starts after the row is written — and then needs a moment to map its result. Ending the row the instant the
    ///     number is reached would race that better answer and sometimes win by milliseconds, throwing the real
    ///     outcome away. This is the backstop for a lane that did not answer its own budget, so it is deliberately
    ///     later than the budget it backs up.
    /// </remarks>
    public static TimeSpan Grace { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether this node run has been running longer than its node allows, by enough that its own lane is not going to answer for it.</summary>
    public static bool HasExpired(GraphWorkflowGraphNode node, GraphWorkflowNodeRunSnapshot nodeRun, GraphWorkflowOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return Expiry(node, nodeRun, options) is { } expiry && timeProvider.GetUtcNow() >= expiry + Grace;
    }

    /// <summary>The absolute instant this attempt is due, or <see langword="null" /> when the row has not started.</summary>
    public static DateTimeOffset? Expiry(GraphWorkflowGraphNode node, GraphWorkflowNodeRunSnapshot nodeRun, GraphWorkflowOptions options)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);
        ArgumentNullException.ThrowIfNull(options);

        var seconds = node.TimeoutSeconds ?? options.DefaultNodeTimeoutSeconds;
        return seconds > 0 && nodeRun.StartedAtUtc is { } startedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(startedAt).AddSeconds(seconds)
            : null;
    }
}
