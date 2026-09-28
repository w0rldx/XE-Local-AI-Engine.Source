namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <param name="Provenance">
///     <c>operator-override</c> | <c>managed-source-build</c> | <c>prebuilt-or-unavailable</c> — the same three values
///     the launch-policy fingerprint commits to.
/// </param>
public sealed record BenchmarkLlamaRuntimeFactsV1(string Version, string Variant, string Provenance, string? SourceCommit);
