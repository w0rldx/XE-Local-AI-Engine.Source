namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Runtime.ExceptionServices;

/// <summary>
///     The outcome of <see cref="LocalRuntimeWarmer.PrepareLocalRuntimeAsync" />: the window the model actually
///     launched with, the warmed provider's name, and the captured failure when readiness failed.
/// </summary>
/// <remarks>
///     The window is null when there was no local warm or it is unknown. The captured failure is what an
///     admission-gated caller must rethrow before its own policy runs.
/// </remarks>
internal readonly record struct LocalRuntimePreparationResult(
    int? EffectiveContextTokens,
    string? ProviderName,
    ExceptionDispatchInfo? WarmFailure);
