namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>The per-session SignalR group every push for one live session goes to.</summary>
internal static class TranscriptionHubGroups
{
    public static string Session(Guid sessionId) =>
        string.Concat("transcription-session-", sessionId.ToString("N"));
}
