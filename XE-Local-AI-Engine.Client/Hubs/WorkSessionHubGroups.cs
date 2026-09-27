namespace XE_Local_AI_Engine.Client.Hubs;

internal static class WorkSessionHubGroups
{
    public static string Session(Guid sessionId) =>
        string.Concat("work-session-", sessionId.ToString("N"));
}
