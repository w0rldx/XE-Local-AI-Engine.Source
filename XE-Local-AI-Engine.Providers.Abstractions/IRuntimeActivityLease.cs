namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>An identity-scoped activity lease. Disposal releases exactly the lease that was granted, once.</summary>
public interface IRuntimeActivityLease : IAsyncDisposable, IDisposable;
