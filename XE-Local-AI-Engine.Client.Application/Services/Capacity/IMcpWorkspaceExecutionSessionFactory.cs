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

internal sealed record McpWorkspaceExecutionSessionOpenResult(
    IMcpWorkspaceExecutionSession? Session,
    string? FailureCode,
    string DisplayMessage)
{
    public static McpWorkspaceExecutionSessionOpenResult Success(IMcpWorkspaceExecutionSession session) =>
        new(session, FailureCode: null, string.Empty);

    public static McpWorkspaceExecutionSessionOpenResult Rejected(string failureCode, string displayMessage) =>
        new(Session: null, FailureCode: failureCode, DisplayMessage: displayMessage);
}
