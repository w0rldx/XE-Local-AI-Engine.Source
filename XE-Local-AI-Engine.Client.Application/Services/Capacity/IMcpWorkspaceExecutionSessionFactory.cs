namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>Authorizes one opaque workspace id under the shared owner-node lease and opens its AgentHome session.</summary>
internal interface IMcpWorkspaceExecutionSessionFactory
{
    Task<McpWorkspaceExecutionSessionOpenResult> OpenAsync(Guid workspaceId,
        CancellationToken cancellationToken);
}

internal interface IMcpWorkspaceExecutionSession : IDisposable
{
    IDisposable EnterAmbientScope();
}

internal sealed class McpWorkspaceExecutionSessionOpenResult
{
    public required IMcpWorkspaceExecutionSession? Session { get; init; }

    public required string? FailureCode { get; init; }

    public required string DisplayMessage { get; init; }

    public static McpWorkspaceExecutionSessionOpenResult Success(IMcpWorkspaceExecutionSession session) =>
        new() { Session = session, FailureCode = null, DisplayMessage = string.Empty };

    public static McpWorkspaceExecutionSessionOpenResult Rejected(string failureCode, string displayMessage) =>
        new() { Session = null, FailureCode = failureCode, DisplayMessage = displayMessage };
}
