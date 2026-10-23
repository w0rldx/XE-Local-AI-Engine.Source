namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Per-user onboarding tour state routes. GET reads the current user's recorded tour entries; PUT upserts one.
    /// </summary>
    public static class Tutorial
    {
        public const string State = "tutorial-state";
    }
}
