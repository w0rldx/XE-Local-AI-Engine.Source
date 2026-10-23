namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Codex (OpenAI ChatGPT subscription) OAuth sign-in routes. The login lifecycle is kept separate from
    ///     the key-based <see cref="CloudSettings" /> surface.
    /// </summary>
    public static class CloudCodex
    {
        public const string Login = "cloud/codex/login";
        public const string Status = "cloud/codex/status";
        public const string Logout = "cloud/codex/logout";
    }
}
