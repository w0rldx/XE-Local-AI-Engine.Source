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

        // One captured block's attention rows of a trace that recorded them: one block per call, because the whole blob is several megabytes.
        public const string ExperimentAttention = "model-lab/experiments/{experimentId:guid}/attention/{layer:int}";

        // A trace's logit lens, apart from the trace: two of the trace's three readers never show it, and it is most of the blob.
        public const string ExperimentLens = "model-lab/experiments/{experimentId:guid}/lens";

        // A trace's router choices per step and MoE block, apart from the trace like the lens: only the stepper's routing panel reads them.
        public const string ExperimentRouting = "model-lab/experiments/{experimentId:guid}/routing";

        public const string ExperimentNotes = "model-lab/experiments/{experimentId:guid}/notes";

        // A pinned experiment is exempt from retention's trim.
        public const string ExperimentPin = "model-lab/experiments/{experimentId:guid}/pin";

        // An installed GGUF's blocks and tensors by component, from the header alone: no model load.
        public const string Architecture = "model-lab/architecture/{modelName}";

        // The model's PyTorch module tree and op graphs, joined to that descriptor: POST builds it (or answers from the cache), GET reads the last one.
        public const string ArchitecturePyTorch = "model-lab/architecture/{modelName}/pytorch";

        // One source file an index names, from the installed runtime's packages and only while its hash matches the index.
        public const string PyTorchSource = "model-lab/pytorch-source";

        // A Next token run's rendered prompt as the running llama-server tokenizes it. Never loads a model.
        public const string PromptTokens = "model-lab/prompt-tokens";

        // The residents widget's eject for the warm worker: frees an Idle worker's checkpoint, never a running job's.
        public const string WarmEject = "model-lab/warm/eject";
    }
}
