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

        // Graceful in-memory unload. The literal "unload" segment follows the model name, mirroring the
        // "kind" route, so it stays distinct from ModelByName.
        public const string Unload = "models/{modelName}/unload";

        // Developer/advanced per-model extra llama-server launch-argument override. The literal "launch-args" segment
        // follows the model name, mirroring "kind"/"unload", so it stays distinct from ModelByName.
        public const string ModelLaunchArguments = "models/{modelName}/launch-args";

        // Read-only header metadata + tensor list of an installed GGUF. The literal "gguf-inspection" segment keeps it distinct from ModelByName.
        public const string GgufInspection = "models/{modelName}/gguf-inspection";
    }
}
