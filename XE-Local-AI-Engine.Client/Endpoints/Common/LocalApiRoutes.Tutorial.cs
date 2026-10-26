namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Per-user onboarding state routes. GET reads the current user's recorded tour entries; PUT upserts one. The AI
    ///     acknowledgement route reads (GET) and records (POST, idempotent) the one-time AI responsibility notice.
    /// </summary>
    public static class Tutorial
    {
        public const string State = "tutorial-state";
        public const string AiAcknowledgement = "tutorial-state/ai-acknowledgement";
    }
}
