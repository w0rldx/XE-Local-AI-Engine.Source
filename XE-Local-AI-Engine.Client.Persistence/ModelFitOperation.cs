namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     The model-fit operation a snapshot captures: a hardware-fit <see cref="Recommend" /> run or a measured
///     <see cref="Benchmark" /> run. The numeric values are persisted, so existing values must never be renumbered.
/// </summary>
public enum ModelFitOperation
{
    Recommend = 0,
    Benchmark = 1
}
