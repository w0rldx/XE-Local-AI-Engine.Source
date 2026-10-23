namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>The operator's support surface: the node-info report and the scrubbed support-bundle zip.</summary>
    public static class Diagnostics
    {
        public const string LogLevel = "diagnostics/log-level";
        public const string NodeInfo = "diagnostics/node-info";
        public const string SupportBundle = "diagnostics/support-bundle";
    }
}
