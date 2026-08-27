namespace XE_Local_AI_Engine.Client.Services.Capacity;

internal sealed partial class SubAgentSpawnService
{
    private async Task<WorkspaceOpenOutcome> OpenWorkspaceAsync(Guid? workspaceId, CancellationToken cancellationToken)
    {
        if (workspaceId is not { } id)
        {
            return new WorkspaceOpenOutcome(Session: null, Failure: null);
        }

        var opened = await _mcpWorkspaceSessionFactory.OpenAsync(id, cancellationToken).ConfigureAwait(false);
        return opened.Session is { } session
            ? new WorkspaceOpenOutcome(session, Failure: null)
            : new WorkspaceOpenOutcome(Session: null,
                Failure: SpawnOutcome.Rejected(opened.FailureCode ?? McpExecutionFailureCodes.WorkspacePreparationFailed,
                    opened.DisplayMessage));
    }

    private sealed record WorkspaceOpenOutcome(IMcpWorkspaceExecutionSession? Session, SpawnOutcome? Failure);
}
