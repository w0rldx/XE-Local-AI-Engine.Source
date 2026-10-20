namespace XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;

/// <summary>One <c>pythonTests</c> verification: the persisted verdict plus both raw sandbox streams.</summary>
/// <remarks>
///     The streams are candidate-controlled and unbounded by the evidence cap; stdout still carries the spent nonce
///     marker. Both are empty when nothing ran (no code, or a harness that could not be composed).
/// </remarks>
public sealed class BenchmarkPythonTestsOutcome
{
    public required BenchmarkJudgeVerifierResultV1 Result { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }
}
