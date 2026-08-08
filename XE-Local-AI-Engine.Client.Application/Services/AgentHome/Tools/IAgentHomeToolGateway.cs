namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools;

/// <summary>
///     Boundary between the <c>run_in_agent_home</c> tool handler and the AgentHome sandbox runtime. The disabled
///     gateway keeps the tool wired, cancellable, and approval-gated while the real <c>IAgentHomeService</c>-backed
///     gateway owns execution when AgentHome is enabled.
/// </summary>
internal interface IAgentHomeToolGateway
{
    Task<string> ExecuteAsync(AgentHomeRunToolRequest request, CancellationToken cancellationToken = default);
}
