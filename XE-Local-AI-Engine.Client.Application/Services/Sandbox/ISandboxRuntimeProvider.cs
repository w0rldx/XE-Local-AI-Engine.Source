namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Provider-neutral runtime over which AgentHome creates a node-scoped sandbox, copies selected folders in,
///     executes commands, reads results, copies artifacts out, and tears the sandbox down (AgentHome plan §6.2). The
///     contract is shaped by AgentHome's lifecycle, not by any provider SDK — no Docker / OpenSandbox / gRPC type
///     appears here. Implementations: <c>FakeSandboxRuntimeProvider</c> (deterministic, CI-mandatory, the MVP
///     default) and, from Marker J-local, a HostAgent-backed local-container provider.
/// </summary>
public interface ISandboxRuntimeProvider
{
    /// <summary>Stable provider identifier (e.g. <c>"fake"</c>, <c>"local-container"</c>) used by configuration-bound selection.</summary>
    string ProviderName { get; }

    /// <summary>The operations this provider can serve. AgentHome gates optional behavior on these flags.</summary>
    SandboxProviderCapabilities Capabilities { get; }

    /// <summary>Create a new sandbox, or attach to the existing one for the same attach key. An owner change forbids reuse.</summary>
    Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reattach to a live sandbox by its attach key; throws <see cref="SandboxHandleInvalidException" /> if none matches.</summary>
    Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default);

    /// <summary>Execute a command inside the sandbox. Honors <paramref name="cancellationToken" /> and per-command cancellation.</summary>
    Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default);

    /// <summary>Copy a file from the host into the sandbox.</summary>
    Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Read a UTF-8 text file from the sandbox.</summary>
    Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default);

    /// <summary>Copy a file out of the sandbox onto the host.</summary>
    Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Best-effort cancel of an in-flight command identified by its execution id.</summary>
    Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default);

    /// <summary>Terminate the sandbox and invalidate the handle. Required for user cancel, reset, and partial-init recovery.</summary>
    Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default);
}
