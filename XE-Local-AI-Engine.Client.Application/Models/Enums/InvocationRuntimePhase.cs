namespace XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     The runtime phase of an in-flight turn, surfaced so the UI can distinguish a legitimate cold-load wait from an
///     apparent hang. The cold-load phases (<see cref="PreparingRuntime" />, <see cref="LoadingModel" />) run BEFORE the
///     stream-idle watchdog is armed — that separation is the fix for the audited "big model killed at 60 s" hang: the
///     model becomes ready first, then generation streams under the watchdog.
/// </summary>
public enum InvocationRuntimePhase
{
    /// <summary>Resolving the local runtime/provider that will serve the turn, before any model weights are loaded.</summary>
    PreparingRuntime = 0,

    /// <summary>Loading the model into the runtime — the cold-start window that precedes the first token.</summary>
    LoadingModel = 1,

    /// <summary>The model is ready and the turn is generating (streaming under the stream-idle watchdog).</summary>
    Generating = 2
}
