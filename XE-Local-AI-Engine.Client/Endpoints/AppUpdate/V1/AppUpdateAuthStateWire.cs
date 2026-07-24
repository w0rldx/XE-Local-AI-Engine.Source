namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

using XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>Maps the internal <see cref="AppUpdateAuthState" /> to its lowercase wire string for the auth/status DTOs.</summary>
internal static class AppUpdateAuthStateWire
{
    public static string Of(AppUpdateAuthState state) =>
        state switch
        {
            AppUpdateAuthState.SignedIn => "signedIn",
            AppUpdateAuthState.ReauthRequired => "reauthRequired",
            AppUpdateAuthState.NoAccess => "noAccess",
            // Wire-only addition: `authState` is a plain string in the DTO (no OpenAPI enum), so a new value here is
            // NOT a schema change and needs no client regeneration — only the React union has to learn about it.
            AppUpdateAuthState.NotConfigured => "notConfigured",
            _ => "signedOut"
        };
}
