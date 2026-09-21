namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Coordinates exclusive access to one owner-node AgentHome sandbox. Acquisition never queues. Code already running
///     inside the same asynchronous owner-node scope may borrow the ambient lease; unrelated callers cannot.
/// </summary>
internal interface IAgentHomeExecutionLeaseManager
{
    IAgentHomeExecutionLease? TryAcquire(AgentHomeExecutionLeaseKey key);

    IAgentHomeExecutionLease? TryAcquireForRecovery(AgentHomeExecutionLeaseKey key);

    /// <summary>
    ///     Whether the gate for <paramref name="key" /> is currently taken, WITHOUT taking it.
    /// </summary>
    /// <remarks>
    ///     For a caller that must leave an in-flight run alone rather than run alongside it — the run-retention
    ///     sweep. The answer is a snapshot and can change the instant it is read, so it is a refusal signal only:
    ///     "held" must stop a destructive action, while "not held" is never a licence to assume exclusivity. Anything
    ///     needing that takes <see cref="TryAcquire" />.
    /// </remarks>
    bool IsHeld(AgentHomeExecutionLeaseKey key);

    bool IsPoisoned(AgentHomeExecutionLeaseKey key);

    void MarkPoisoned(AgentHomeExecutionLeaseKey key);

    void ClearPoison(AgentHomeExecutionLeaseKey key);
}

internal interface IAgentHomeExecutionLease : IDisposable
{
    bool IsBorrowed { get; }

    IDisposable EnterAmbientScope();
}

internal readonly record struct AgentHomeExecutionLeaseKey(string OwnerUserId, string NodeId);
