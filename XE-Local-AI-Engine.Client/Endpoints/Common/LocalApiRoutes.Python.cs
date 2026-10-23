namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>Managed Python status (the shared uv toolchain and each feature environment) and the Compute environment actions.</summary>
    public static class Python
    {
        public const string Status = "python/status";
        public const string ComputeRepair = "python/environments/compute/repair";
        public const string ComputeRemove = "python/environments/compute/remove";
    }
}
