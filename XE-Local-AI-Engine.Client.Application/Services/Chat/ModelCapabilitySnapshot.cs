namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     One model's advertised <c>thinking</c>/<c>tools</c> capabilities plus its provider locality, all resolved from a
///     single provider-routing decision. <see cref="IsCloud" /> feeds ONLY the private-data gates; the capability flags
///     are independent of it, so a fail-closed locality never disturbs reasoning or tool detection.
/// </summary>
public sealed record ModelCapabilitySnapshot(bool SupportsThinking, bool SupportsTools, bool IsCloud);
