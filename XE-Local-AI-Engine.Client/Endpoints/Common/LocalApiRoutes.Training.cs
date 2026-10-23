namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Training group routes. The dataset half (definitions, datasets, samples, mocks) is declared here; the
    ///     runtime and base-artifact halves append their own constants.
    /// </summary>
    public static class Training
    {
        public const string Definitions = "training/definitions";
        public const string DefinitionById = "training/definitions/{definitionId}";
        public const string DefinitionGenerate = "training/definitions/{definitionId}/generate";
        public const string Datasets = "training/datasets";
        public const string DatasetById = "training/datasets/{datasetId}";
        public const string DatasetSamples = "training/datasets/{datasetId}/samples";
        public const string DatasetSampleById = "training/datasets/{datasetId}/samples/{sampleId}";
        public const string DatasetExport = "training/datasets/{datasetId}/export";
        public const string DatasetCancel = "training/datasets/{datasetId}/cancel";
        public const string Mocks = "training/mocks";
        public const string MockById = "training/mocks/{mockId}";
        public const string MockVerify = "training/mocks/{mockId}/verify";
        public const string DatasetGenerationHub = "/api/local/v1/training/datasets/hub";

        // Python training runtime (uv-managed venv). One machine-global runtime, so none of these are id-scoped.
        public const string RuntimeStatus = "training/runtime/status";
        public const string RuntimePrerequisites = "training/runtime/prerequisites";
        public const string RuntimeInstall = "training/runtime/install";
        public const string RuntimeRemove = "training/runtime/remove";

        /// <summary>SignalR push hub for training-runtime install phase and log changes.</summary>
        public const string RuntimeHub = "/api/local/v1/training/runtime/hub";

        // Base checkpoints downloaded from Hugging Face.
        public const string BaseArtifacts = "training/base-artifacts";
        public const string BaseArtifactById = "training/base-artifacts/{artifactId}";
        public const string BaseArtifactCancel = "training/base-artifacts/{artifactId}/cancel";
        public const string BaseArtifactLicense = "training/base-artifacts/{artifactId}/license";

        // Training runs. The queue is single-consumer, so create only enqueues — the run starts once the GPU is free.
        public const string Runs = "training/runs";
        public const string RunById = "training/runs/{runId}";
        public const string RunCancel = "training/runs/{runId}/cancel";

        /// <summary>Computed hyper-parameters plus the VRAM estimate and the licensing text the run wizard renders.</summary>
        public const string RunDefaults = "training/runs/defaults";

        /// <summary>SignalR push hub for per-run status, phase and training progress — evaluation progress rides it too.</summary>
        public const string RunHub = "/api/local/v1/training/runs/hub";

        // Evaluation runs. They ride the same single-consumer queue as training runs, so create only enqueues.
        public const string Evaluations = "training/evaluations";
        public const string EvaluationById = "training/evaluations/{evaluationId}";
        public const string EvaluationResume = "training/evaluations/{evaluationId}/resume";
        public const string EvaluationCancel = "training/evaluations/{evaluationId}/cancel";

        // Comparison reports over two evaluation runs.
        public const string Comparisons = "training/comparisons";
        public const string ComparisonById = "training/comparisons/{comparisonId}";

        /// <summary>Lineage auto-suggest: the two model names and evaluations one training run implies.</summary>
        public const string ComparisonSuggest = "training/comparisons/suggest";

        // Exports. Starting one and listing what a run produced are run-scoped; every action ON an artifact addresses it by its own id, because an artifact outlives the
        // export that produced it and is acted on without the run in hand.
        public const string RunExports = "training/runs/{runId}/exports";
        public const string RunArtifacts = "training/runs/{runId}/artifacts";
        public const string ArtifactById = "training/artifacts/{artifactId}";
        public const string ArtifactSmoke = "training/artifacts/{artifactId}/smoke";
        public const string ArtifactPromote = "training/artifacts/{artifactId}/promote";
        public const string ArtifactQuality = "training/artifacts/{artifactId}/quality";
        public const string ArtifactQualityRevalidation = "training/artifacts/{artifactId}/quality/revalidation";
        public const string ArtifactQualityOverride = "training/artifacts/{artifactId}/quality/override";
        public const string ArtifactQualityDiscard = "training/artifacts/{artifactId}/quality/discard";
    }
}
