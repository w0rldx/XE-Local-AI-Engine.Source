namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     App self-update routes (Velopack). Status reads the cached snapshot (<c>?refresh=true</c>: check, 10-minute
    ///     floor); release-notes reads that check's notes; apply relaunches into the update. Desktop-only; Operator.
    /// </summary>
    public static class AppUpdate
    {
        public const string Status = "app-update/status";
        public const string ReleaseNotes = "app-update/release-notes";
        public const string Apply = "app-update/apply";
        public const string Channel = "app-update/channel";
    }
}
