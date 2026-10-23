namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class CloudSettings
    {
        public const string Settings = "cloud-settings";

        // Entra ID device-code sign-in for the stored Azure Foundry connection (interactive sign-in, no client secret). Under CloudSettings, not CloudCodex, because it authenticates
        // that connection rather than a separate provider. No token material crosses: start returns only the user code + verification URL, status the state and those same non-secret fields.
        public const string EntraDeviceCodeStart = "cloud-settings/entra/device-code/start";
        public const string EntraDeviceCodeStatus = "cloud-settings/entra/device-code/status";

        // Entra ID authorization-code sign-in (confidential client + PKCE, Postman parity): browser sign-in yields a delegated token while the stored client secret
        // authenticates the code redemption. Start returns only the authorize URL to open, status the lifecycle state. Never exposes token material.
        public const string EntraAuthCodeStart = "cloud-settings/entra/auth-code/start";
        public const string EntraAuthCodeStatus = "cloud-settings/entra/auth-code/status";
    }
}
