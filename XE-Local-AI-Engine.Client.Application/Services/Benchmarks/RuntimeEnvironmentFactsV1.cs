namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     What the node looked like immediately before a benchmark run's llama-server spawn: the selected runtime bundle,
///     the host hardware, and the installed llama.cpp runtime's provenance.
/// </summary>
/// <remarks>
///     Facts only — this record makes no claim that two runs carrying the same facts are comparable. A capture never
///     fails the run, so an empty <paramref name="Missing" /> is the only proof that everything was observed, and it
///     IS part of the hash: a part that could not be read is itself an environmental fact.
///     <paramref name="CapturedAtUtc" /> is persisted but deliberately NOT part of <c>EnvironmentFactsHash</c> — that
///     hash answers "is this the same environment?", and a wall clock would make two runs of one node differ.
/// </remarks>
/// <param name="Missing">
///     The parts that could not be captured, by name (<c>runtimeBundle</c>, <c>hardware</c>, <c>llamaRuntime</c>).
/// </param>
/// <param name="CapturedAtUtc">When this capture was taken.</param>
public sealed record RuntimeEnvironmentFactsV1(
    int SchemaVersion,
    RuntimeBundleFactsV1? RuntimeBundle,
    BenchmarkHardwareFactsV1? Hardware,
    BenchmarkLlamaRuntimeFactsV1? LlamaRuntime,
    long CapturedAtUtc,
    IReadOnlyList<string> Missing);
