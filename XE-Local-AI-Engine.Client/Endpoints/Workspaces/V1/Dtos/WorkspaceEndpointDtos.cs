namespace XE_Local_AI_Engine.Client.Endpoints.Workspaces.V1;

public sealed class CreateWorkspaceRequest
{
    public string? Alias { get; init; }

    public string? HostPath { get; init; }
}

public sealed class DeleteWorkspaceRequest
{
    public string WorkspaceId { get; init; } = string.Empty;
}

public sealed class WorkspaceResponse
{
    public required string WorkspaceId { get; init; }

    public required string Alias { get; init; }

    public string Mode { get; init; } = "read-only";
}

public sealed class ListWorkspacesResponse
{
    public IReadOnlyList<WorkspaceResponse> Items { get; init; } = [];
}
