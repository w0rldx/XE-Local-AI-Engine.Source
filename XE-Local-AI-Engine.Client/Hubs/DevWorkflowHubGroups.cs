namespace XE_Local_AI_Engine.Client.Hubs;

internal static class DevWorkflowHubGroups
{
    public static string Run(Guid runId) =>
        string.Concat("dev-workflow-run-", runId.ToString("N"));
}
