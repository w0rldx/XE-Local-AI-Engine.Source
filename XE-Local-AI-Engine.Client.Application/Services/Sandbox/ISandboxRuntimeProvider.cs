namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using System.Text;

/// <summary>
///     Provider-neutral runtime over which AgentHome creates a node-scoped sandbox, copies selected folders in,
///     executes commands, reads results, copies artifacts out, and tears the sandbox down. The
///     contract is shaped by AgentHome's lifecycle, not by any provider SDK — no Docker / OpenSandbox / gRPC type
///     appears here. Implementations: <c>FakeSandboxRuntimeProvider</c> (deterministic, CI-mandatory, the safe default),
///     <c>ProcessSandboxRuntimeProvider</c> (a jailed supervised-child process), and <c>DockerSandboxRuntimeProvider</c>
///     (a hardened container, Development Mode only per ADR 0004).
///     <para>
///         This interface is deliberately NOT registered in DI, and nothing injects it. Consumers take one of the two
///         role-scoped markers instead — <see cref="IAgentSandboxRuntimeProvider" /> or
///         <see cref="IDevelopmentSandboxRuntimeProvider" /> — because provider selection is per feature. This stays
///         the shared contract those roles are expressed in, and the seam a future hardware-isolated
///         (MXC) provider slots into.
///     </para>
/// </summary>
public interface ISandboxRuntimeProvider
{
    /// <summary>Stable provider identifier (e.g. <c>"fake"</c>, <c>"process"</c>) used by configuration-bound selection.</summary>
    string ProviderName { get; }

    /// <summary>The operations this provider can serve. AgentHome gates optional behavior on these flags.</summary>
    SandboxProviderCapabilities Capabilities { get; }

    /// <summary>Create a new sandbox, or attach to the existing one for the same attach key. An owner change forbids reuse.</summary>
    Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reattach to a live sandbox by its attach key; throws <see cref="SandboxHandleInvalidException" /> if none matches.</summary>
    Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default);

    /// <summary>Execute a command inside the sandbox. Honors <paramref name="cancellationToken" /> and per-command cancellation.</summary>
    Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Start a LONG-LIVED command inside the sandbox and hand back its standard streams, for a caller that speaks
    ///     a duplex protocol to it rather than reading a result. <see cref="SandboxCommandRequest.StandardInput" /> and
    ///     <see cref="SandboxCommandRequest.Timeout" /> are ignored: the caller owns stdin, and a protocol peer has no
    ///     per-call deadline.
    ///     <para>
    ///         The default throws, like <see cref="ListFilesAsync" /> and <see cref="SearchTextAsync" />: a provider
    ///         that has not implemented it must refuse rather than fall back to a host launch, which is the exact
    ///         degradation the one caller — a <c>Sandboxed</c> stdio MCP server — exists to prevent.
    ///     </para>
    /// </summary>
    Task<ISandboxInteractiveProcess> StartInteractiveAsync(SandboxHandle handle,
        SandboxCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        throw new SandboxCapabilityNotSupportedException($"The '{ProviderName}' provider cannot host a long-lived interactive command.");
    }

    /// <summary>Copy a file from the host into the sandbox.</summary>
    Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Read a UTF-8 text file from the sandbox.</summary>
    Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default);

    /// <summary>Read a UTF-8 text file without capturing more than <paramref name="maxBytes" />.</summary>
    async Task<string> ReadFileAsync(SandboxHandle handle,
        string sandboxPath,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        var content = await ReadFileAsync(handle, sandboxPath, cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(content) > maxBytes)
        {
            throw new InvalidDataException("The sandbox file exceeds the requested read bound.");
        }

        return content;
    }

    /// <summary>
    ///     List the regular files under a sandbox directory, as <c>./relative/path</c> entries.
    ///     <para>
    ///         A provider operation rather than a command the caller composes, for the same reason
    ///         <see cref="ReadFileAsync(SandboxHandle, string, CancellationToken)" /> is one: only the provider knows how
    ///         a sandbox path maps to bytes, and only the provider can apply its own confinement to that mapping. The
    ///         callers used to shell out to <c>find</c> instead, which meant the operation did not exist at all on a host
    ///         without GNU findutils — on stock Windows 11, <c>find</c> resolves to the DOS tool and rejects the
    ///         argument vector outright.
    ///     </para>
    ///     <para>
    ///         The default throws: a provider that has not implemented the survey must refuse it rather than return an
    ///         empty listing, which a caller would read as "the workspace is empty". Implemented by the providers that
    ///         serve the agent role; the container provider does not, because nothing asks it to.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<string>> ListFilesAsync(SandboxHandle handle, SandboxListFilesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        throw new SandboxCapabilityNotSupportedException($"The '{ProviderName}' provider cannot list sandbox files.");
    }

    /// <summary>
    ///     Search the non-binary regular files under a sandbox directory, as <c>./relative/path:line:text</c> entries.
    ///     Replaces a <c>grep</c> shell-out for the same reasons as <see cref="ListFilesAsync" /> — and <c>grep</c> does
    ///     not exist at all on a stock Windows 11 install.
    /// </summary>
    Task<IReadOnlyList<string>> SearchTextAsync(SandboxHandle handle, SandboxSearchTextRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        throw new SandboxCapabilityNotSupportedException($"The '{ProviderName}' provider cannot search sandbox files.");
    }

    /// <summary>Copy a file out of the sandbox onto the host.</summary>
    Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Best-effort cancel of an in-flight command identified by its execution id.</summary>
    Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default);

    /// <summary>Terminate the sandbox and invalidate the handle. Required for user cancel, reset, and partial-init recovery.</summary>
    Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default);
}
