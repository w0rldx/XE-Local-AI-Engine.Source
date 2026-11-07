namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>Local API contract type for Model Lab's tracing and steering experiments.</summary>
    public static class ModelLab
    {
        // Answers "can this checkpoint be traced on this card and paired with this GGUF?" before any weight is transferred.
        public const string CheckpointPreflight = "model-lab/checkpoint-preflight";

        // The preflight, then the license gate, then the base-checkpoint download. A refused pick fetches no weights.
        public const string CheckpointDownload = "model-lab/checkpoint-download";

        public const string Experiments = "model-lab/experiments";

        public const string Experiment = "model-lab/experiments/{experimentId:guid}";

        public const string ExperimentCancel = "model-lab/experiments/{experimentId:guid}/cancel";

        public const string ExperimentTrace = "model-lab/experiments/{experimentId:guid}/trace";

        // An installed GGUF's blocks and tensors by component, from the header alone: no model load.
        public const string Architecture = "model-lab/architecture/{modelName}";

        // The model's PyTorch module tree and op graphs, joined to that descriptor: POST builds it (or answers from the cache), GET reads the last one.
        public const string ArchitecturePyTorch = "model-lab/architecture/{modelName}/pytorch";

        // One source file an index names, from the installed runtime's packages and only while its hash matches the index.
        public const string PyTorchSource = "model-lab/pytorch-source";
    }
}
