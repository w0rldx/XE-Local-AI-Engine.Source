namespace XE_Local_AI_Engine.Client.Hubs;

internal static class ExternalAppHubGroups
{
    public static string Instance(Guid instanceId) =>
        string.Concat("external-app-", instanceId.ToString("N"));
}
