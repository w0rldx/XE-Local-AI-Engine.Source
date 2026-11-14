namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

/// <summary>
///     What an agent's effective model is, and whether the tool gates a work session depends on admit it.
/// </summary>
/// <remarks>
///     <see cref="SupportsTools" /> is the model's own capability, detected from its chat template or the declared
///     cloud matrix; <see cref="IsAllowListed" /> is the operator's tool-capable allow-list. They come from different
///     sources and are free to disagree, which is why both are reported. <see cref="SupportsTools" /> is
///     <see langword="null" /> when the probe was NOT run (<see cref="WorkSessionToolGate.InspectAllowListAsync" />):
///     "not asked" and "asked, and the model cannot" are different facts.
/// </remarks>
internal readonly record struct WorkSessionToolGateVerdict(
    bool AgentExists,
    string AgentName,
    string? EffectiveModel,
    bool? SupportsTools,
    bool IsAllowListed)
{
    /// <summary>How a refusal names the model, which has to name the thing the operator would go and change.</summary>
    public string Subject => $"'{AgentName}' runs on '{EffectiveModel}'";
}
