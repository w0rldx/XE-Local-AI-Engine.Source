namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>Why a <see cref="InvocationRuntimePhase.LoadingModel" /> phase loads the model, and how long its last load took.</summary>
public sealed record InvocationRuntimePhaseDetail
{
    /// <summary>One of the <see cref="InvocationRuntimePhaseReasons" /> values, or null when unknown.</summary>
    public string? Reason { get; init; }

    /// <summary>The readiness duration of this model's previous load in this process, or null when none.</summary>
    public double? LastLoadMs { get; init; }
}
