namespace XE_Local_AI_Engine.Client.Hubs;

internal static class GraphWorkflowHubGroups
{
    public static string Run(Guid runId) =>
        string.Concat("graph-workflow-run-", runId.ToString("N"));
}
