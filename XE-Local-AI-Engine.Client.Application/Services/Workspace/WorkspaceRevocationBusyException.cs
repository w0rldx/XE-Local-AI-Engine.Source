namespace XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>Stable workspace-domain conflict raised when the owner/node execution lease is already held.</summary>
public sealed class WorkspaceRevocationBusyException : Exception
{
    public const string ErrorCode = "workspace_busy";

    public WorkspaceRevocationBusyException()
        : base("The workspace is busy and cannot be revoked yet.")
    {
    }

    public WorkspaceRevocationBusyException(string message)
        : base(message)
    {
    }

    public WorkspaceRevocationBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
