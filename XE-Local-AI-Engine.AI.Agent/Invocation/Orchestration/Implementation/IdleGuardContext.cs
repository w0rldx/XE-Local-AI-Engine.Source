namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;

/// <summary>
///     The idle-guard's parameters, bundled so no method carries multiple loose <see cref="CancellationToken" />s.
/// </summary>
/// <remarks>
///     The two tokens are kept last so a single loose token would still satisfy the analyzer.
///     <paramref name="OnIdleTimeout" /> fires once when the deadline stops the run and
///     <paramref name="OnAbandoned" /> once per abandoned advancement or disposal.
///     <paramref name="IdleToken" /> is the caller's re-armable deadline, linked by the caller to
///     <paramref name="OuterToken" />, which tells an idle timeout apart from a plain cancellation.
/// </remarks>
internal readonly record struct IdleGuardContext(
    TimeSpan Grace,
    Action OnIdleTimeout,
    Action OnAbandoned,
    CancellationToken IdleToken,
    CancellationToken OuterToken);
