namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class Auth
    {
        public const string Status = "auth/status";
        public const string Setup = "auth/setup";
        public const string Login = "auth/login";
        public const string Refresh = "auth/refresh";
        public const string Logout = "auth/logout";
        public const string ChangePassword = "auth/change-password";
        public const string VaultConfirm = "auth/vault/confirm";
        public const string Sessions = "auth/sessions";
        public const string SessionById = "auth/sessions/{sessionId}";
        public const string RevokeAllSessions = "auth/sessions/revoke-all";

        // Served only by the locked pre-host, before the real host exists.
        public const string VaultUnlock = "auth/vault/unlock";
        public const string VaultUnlockRecovery = "auth/vault/unlock-recovery";
    }
}
