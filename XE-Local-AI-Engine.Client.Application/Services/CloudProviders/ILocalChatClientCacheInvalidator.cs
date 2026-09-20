namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Invalidates the local-branch chat-client cache held by the model-routing local chat client.
/// </summary>
/// <remarks>
///     The router caches a deferred chat client per <c>(provider, model)</c>, and each cached llama-server client
///     resolves its localhost endpoint once. When the operator switches or updates the llama.cpp runtime variant that
///     endpoint is gone, so the cache is cleared to force re-resolution, which ensure-runs the backing process against
///     the freshly installed binary on the next send. Implemented by the singleton router and exposed as its own
///     service so scoped consumers (the runtime-update endpoint) need no captive dependency on the router.
/// </remarks>
public interface ILocalChatClientCacheInvalidator
{
    /// <summary>
    ///     Clears every cached <c>(provider, model)</c> chat client and disposes it. Thread-safe and idempotent; the next
    ///     send re-resolves a fresh client. The underlying model processes are owned by the supervisor and are NOT torn
    ///     down by this call.
    /// </summary>
    void ClearClientCache();
}
