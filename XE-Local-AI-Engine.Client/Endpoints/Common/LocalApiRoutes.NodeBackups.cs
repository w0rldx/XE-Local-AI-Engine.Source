namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>Node database snapshots: list, take one now, and stage a restore applied at the next start.</summary>
    public static class NodeBackups
    {
        public const string Backups = "node/backups";
        public const string Restore = "node/backups/{name}/restore";
    }
}
