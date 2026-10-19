namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The answer a decision model gave to the one question, as llama-server reported it.</summary>
/// <remarks>
///     Every number is model-reported and scaled by temperatures stored in the model file; upstream does not guarantee
///     calibration, so these are routing signals, not probabilities in the statistical sense.
/// </remarks>
public sealed class LlamaServerSystemOneAnswer
{
    /// <summary>The question type the server answered (<c>choice</c>, <c>noul</c>, <c>score</c>), verbatim.</summary>
    public required string Type { get; init; }

    public string? Choice { get; init; }

    public double? Noul { get; init; }

    public double? Score { get; init; }

    /// <summary>Score only: level index (as a string) to level name.</summary>
    public IReadOnlyDictionary<string, string>? Legend { get; init; }

    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }

    public double? Confidence { get; init; }
}
