namespace XE_Local_AI_Engine.Client.Services.AgentHome;

using XE_Local_AI_Engine.Client.Services.Sandbox;

internal interface IAgentHomeWorkspaceIsolation
{
    Task<AgentHomeWorkspaceClearResult> ClearAsync(SandboxHandle handle,
        AgentHomeExecutionLeaseKey key,
        CancellationToken cancellationToken = default);

    Task RecoverExistingAsync(SandboxAttachKey attachKey,
        AgentHomeExecutionLeaseKey key,
        CancellationToken cancellationToken = default);
}

internal enum AgentHomeWorkspaceClearResult
{
    Reset,
    SandboxKilled
}

internal sealed class AgentHomeWorkspacePoisonedException : InvalidOperationException
{
    private const string DefaultMessage =
        "The AgentHome workspace could not be isolated safely and is unavailable until recovery succeeds.";

    public AgentHomeWorkspacePoisonedException()
        : base(DefaultMessage)
    {
    }

    public AgentHomeWorkspacePoisonedException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }

    public AgentHomeWorkspacePoisonedException(string message)
        : base(message)
    {
    }

    public AgentHomeWorkspacePoisonedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
