namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <param name="Name">Raw model name/tag as reported by the runtime (caller normalizes).</param>
/// <param name="Digest">Raw content digest as reported by the runtime, when available.</param>
public sealed record InstalledModelEntry(string? Name, string? Digest);
