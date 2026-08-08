namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Per-process health snapshot for one running <c>(model, role)</c> llama-server, aggregated by the supervisor
///     into the single provider-level <c>ModelProviderHealth</c>.
/// </summary>
/// <param name="ModelName">Model the process serves.</param>
/// <param name="Role">Role the process serves.</param>
/// <param name="IsResponsive">Whether the process answered its health probe.</param>
/// <param name="Detail">A sanitized, user-safe diagnostic line (no internal paths/secrets).</param>
public sealed record LlamaServerProcessHealth(string ModelName, ModelRole Role, bool IsResponsive, string Detail);
