namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

// The KV-cache type a launch will actually use: the effective type, whether it was picked explicitly or resolved by
// Auto, and — for Auto — the reason it degraded (null when it did not).
internal sealed record KvCacheResolution(string Effective, string Source, string? Reason);
