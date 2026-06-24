namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Invalidates the local-branch chat-client cache held by the model-routing local chat client. The router caches a
///     deferred chat client per <c>(provider, model)</c>; each cached llama-server client resolves its localhost
///     endpoint once and reuses it. When the operator switches/updates the llama.cpp runtime variant the previously
///     resolved endpoint is gone, so the cache must be cleared to force re-resolution (which ensure-runs the backing
///     process against the freshly installed binary) on the next send.
/// </summary>
/// <remarks>
///     Implemented by the singleton local chat-client router and exposed as its own service so scoped consumers (e.g. the
///     runtime-update endpoint) can trigger invalidation without taking a captive dependency on the router itself.
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
