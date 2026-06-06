namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

/// <summary>Result of a model-runtime reachability/version probe.</summary>
/// <param name="Reachable">True when the runtime endpoint responded as running.</param>
/// <param name="Version">Normalized runtime version string when reachable; otherwise <c>null</c>.</param>
/// <param name="Diagnostics">Diagnostics raised while probing (for example runtime-unreachable).</param>
internal sealed record OllamaRuntimeStatus(bool Reachable, string? Version, IReadOnlyList<string> Diagnostics);
