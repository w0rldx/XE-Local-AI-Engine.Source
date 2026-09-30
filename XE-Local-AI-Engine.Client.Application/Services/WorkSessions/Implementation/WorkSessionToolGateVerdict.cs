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
    bool IsAllowListed,
    bool ModelIsCallerPinned = false)
{
    /// <summary>How a refusal names the model, which has to name the thing the operator would go and change.</summary>
    /// <remarks>
    ///     A caller pin — a development-workflow node's <c>modelProfile</c> — is not something the agent "runs on": the
    ///     agent may be pinned to something else, and the agent's settings would be the wrong screen.
    /// </remarks>
    public string Subject =>
        ModelIsCallerPinned
            ? $"This work session is pinned to '{EffectiveModel}'"
            : $"'{AgentName}' runs on '{EffectiveModel}'";

    /// <summary>Where the model that was refused can be changed, which follows from the same distinction.</summary>
    public string Remedy =>
        ModelIsCallerPinned
            ? "pin a listed model instead"
            : "pick an agent on a listed model";
}
