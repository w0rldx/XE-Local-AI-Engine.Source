namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Coordinates exclusive access to one owner-node AgentHome sandbox. Acquisition never queues. Code already running
///     inside the same asynchronous owner-node scope may borrow the ambient lease; unrelated callers cannot.
/// </summary>
internal interface IAgentHomeExecutionLeaseManager
{
    IAgentHomeExecutionLease? TryAcquire(AgentHomeExecutionLeaseKey key);

    IAgentHomeExecutionLease? TryAcquireForRecovery(AgentHomeExecutionLeaseKey key);

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
