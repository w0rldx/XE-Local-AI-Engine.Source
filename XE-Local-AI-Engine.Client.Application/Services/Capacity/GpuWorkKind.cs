namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>What is holding the node's GPU. The tag is carried so a refusal can name the holder.</summary>
public enum GpuWorkKind
{
    TrainingRun,
    EvaluationRun,
    Export,
    Benchmark,
    DatasetGeneration,
    ImageJob
}
