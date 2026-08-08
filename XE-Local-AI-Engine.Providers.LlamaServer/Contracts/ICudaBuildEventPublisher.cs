namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Publishes in-app CUDA build progress to connected operator clients. The default implementation
///     (<see cref="Implementation.NullCudaBuildEventPublisher" />) is a no-op; the Client host swaps in a hub-backed
///     publisher (<c>CudaBuildEventPublisher</c> over the <c>CudaBuildHub</c>), mirroring the GGUF download hub.
/// </summary>
/// <remarks>
///     Build logs are Operator-only, ephemeral UI state (never persisted/telemetered) and are produced under a scrubbed
///     environment so they carry no app secrets; the build service still redacts the cache-root/HOME prefix from streamed
///     lines. They are NOT claimed to be fully sanitized raw compiler output. <c>[secLOW-1]</c>
/// </remarks>
public interface ICudaBuildEventPublisher
{
    /// <summary>Pushes the latest build phase + appended log lines to all connected operator clients.</summary>
    Task PublishStatusAsync(CudaBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default);
}

/// <summary>
///     Stable SignalR client-method name for CUDA build status pushes. The React client subscribes to this single method.
/// </summary>
public static class CudaBuildHubEvents
{
    /// <summary>The client method name a CUDA build status push is broadcast under.</summary>
    public const string StatusChanged = "cudaBuild.statusChanged";
}

/// <summary>
///     A CUDA build status push payload: the current <see cref="Phase" /> name, the log lines appended since the previous
///     push, whether this is a <see cref="Terminal" /> event (Completed/Cancelled/Failed), and a sanitized error on
///     failure. Log lines carry no app secrets (scrubbed-env build) and have the cache-root/HOME prefix redacted.
/// </summary>
/// <param name="Phase">The <see cref="CudaBuildPhase" /> name.</param>
/// <param name="AppendedLogLines">Log lines produced since the previous push.</param>
/// <param name="Terminal">True when the build has finished (Completed/Cancelled/Failed).</param>
/// <param name="SanitizedError">A user-safe error reason when the build failed; otherwise null.</param>
public sealed record CudaBuildStatusHubEvent(
    string Phase,
    IReadOnlyList<string> AppendedLogLines,
    bool Terminal,
    string? SanitizedError);
