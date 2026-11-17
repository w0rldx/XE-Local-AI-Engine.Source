namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>One read of the warm Model Lab worker, for the residents widget.</summary>
public sealed class ModelLabWarmResident
{
    /// <summary><see cref="RuntimeResidentState.Starting" />, <see cref="RuntimeResidentState.Active" /> or <see cref="RuntimeResidentState.Idle" />.</summary>
    public required RuntimeResidentState State { get; init; }

    /// <summary>The Hugging Face repository id of the checkpoint the worker loads or holds.</summary>
    public required string ModelId { get; init; }
}
