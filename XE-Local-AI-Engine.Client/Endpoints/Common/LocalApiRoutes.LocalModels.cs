namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class LocalModels
    {
        public const string Models = "models";
        public const string ModelByName = "models/{modelName}";
        public const string ModelDetails = "models/{modelName}/details";
        public const string Select = "models/select";

        // Operator override of model classification. The literal "kind" segment keeps this distinct from ModelByName.
        public const string ModelKind = "models/{modelName}/kind";

        // Currently loaded (in-memory) models the runtime reports via /api/ps. The literal "running" segment after
        // "models" keeps it distinct from the {modelName} route param so it is never parsed as a model name.
        public const string Running = "models/running";

        // Graceful in-memory unload (keep_alive=0). The literal "unload" segment follows the model name, mirroring the
        // "kind" route, so it stays distinct from ModelByName.
        public const string Unload = "models/{modelName}/unload";

        // Developer/advanced per-model extra llama-server launch-argument override. The literal "launch-args" segment
        // follows the model name, mirroring "kind"/"unload", so it stays distinct from ModelByName.
        public const string ModelLaunchArguments = "models/{modelName}/launch-args";
    }
}
