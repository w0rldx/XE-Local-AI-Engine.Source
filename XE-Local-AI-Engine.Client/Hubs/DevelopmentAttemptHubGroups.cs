namespace XE_Local_AI_Engine.Client.Hubs;

internal static class DevelopmentAttemptHubGroups
{
    public static string Attempt(Guid projectId, Guid attemptId) =>
        string.Concat("development-project:", projectId.ToString("N"), ":attempt:", attemptId.ToString("N"));
}
