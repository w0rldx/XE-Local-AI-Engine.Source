namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools;

/// <summary>
///     Boundary between the <c>run_in_agent_home</c> tool handler and the real AgentHome sandbox runtime. Marker B
///     ships a pending placeholder (the tool is wired, cancellable, and approval-gated, but the sandbox body does
///     not exist yet); Marker I replaces the registration with the real <c>IAgentHomeService</c>-backed gateway.
/// </summary>
internal interface IAgentHomeToolGateway
{
    Task<string> ExecuteAsync(AgentHomeRunToolRequest request, CancellationToken cancellationToken = default);
}
