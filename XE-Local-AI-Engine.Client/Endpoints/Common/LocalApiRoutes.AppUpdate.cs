namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     App self-update routes (Velopack). Status reads the cached snapshot (<c>?refresh=true</c> forces a check with a
    ///     10-minute floor); apply downloads + applies + relaunches. Desktop-mode only;
    ///     Operator-gated.
    /// </summary>
    public static class AppUpdate
    {
        public const string Status = "app-update/status";
        public const string Apply = "app-update/apply";
        public const string Channel = "app-update/channel";
    }
}
