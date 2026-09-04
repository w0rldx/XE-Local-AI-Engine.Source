namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Per-process health snapshot for one running <c>(model, role)</c> llama-server, aggregated by the supervisor
///     into the single provider-level <c>ModelProviderHealth</c>.
/// </summary>
/// <param name="ModelName">Model the process serves.</param>
/// <param name="Role">Role the process serves.</param>
/// <param name="IsResponsive">Whether the process answered its health probe.</param>
/// <param name="Detail">A sanitized, user-safe diagnostic line (no internal paths/secrets).</param>
/// <param name="HasExited">
///     Whether the OS process behind this entry is GONE. Distinct from <paramref name="IsResponsive" />, which is
///     also false for a process that is alive but still loading or wedged: an exited entry holds no VRAM, no port and
///     no loaded-process slot, and lingers in the table only until the idle reaper collects it. Callers that decide
///     capacity or routing from this snapshot must treat it as NOT running; callers that merely report health show it
///     as the exited process it is. Trailing optional so every existing construction is unchanged.
/// </param>
public sealed record LlamaServerProcessHealth(string ModelName, ModelRole Role, bool IsResponsive, string Detail, bool HasExited = false);
