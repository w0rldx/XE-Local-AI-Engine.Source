namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;

/// <summary>
///     Marker B placeholder for <see cref="IAgentHomeToolGateway" />. Honors cancellation and returns a clear
///     "not yet available" result so the wired tool is observable end-to-end without any sandbox runtime. Marker I
///     replaces this registration with the real sandbox-backed gateway.
/// </summary>
internal sealed class PendingAgentHomeToolGateway : IAgentHomeToolGateway
{
    public Task<string> ExecuteAsync(AgentHomeRunToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            "AgentHome execution is not yet available on this node. The run_in_agent_home tool is wired " +
            "(resolved, cancellable, and approval-gated) but its sandbox runtime ships in a later milestone (Marker I).");
    }
}
