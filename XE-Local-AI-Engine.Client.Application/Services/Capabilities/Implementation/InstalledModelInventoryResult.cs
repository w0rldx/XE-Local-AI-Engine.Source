namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

/// <summary>Result of an installed-model inventory probe.</summary>
/// <param name="Models">Discovered + configured-fallback models, normalized, deduped and ordered.</param>
/// <param name="OllamaQuerySucceeded">True when the runtime inventory query succeeded (false on transport failure).</param>
/// <param name="Diagnostics">Diagnostics raised while probing (for example runtime-unreachable).</param>
internal sealed record InstalledModelInventoryResult(IReadOnlyList<InstalledModelInfo> Models, bool OllamaQuerySucceeded, IReadOnlyList<string> Diagnostics);
