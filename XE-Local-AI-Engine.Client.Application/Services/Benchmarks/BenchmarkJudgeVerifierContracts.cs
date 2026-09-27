namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Text normalization applied before an <c>exact</c> comparison. Defaults trim and nothing else.</summary>
public sealed record BenchmarkVerifierNormalizeV1(
    bool Trim = true,
    bool CollapseWhitespace = false,
    bool CaseInsensitive = false,
    bool StripMarkdown = false);

/// <summary>IFEval-style structural constraints. Language detection is intentionally outside this contract.</summary>
public sealed record BenchmarkConstraintConfigV1(
    int? MinWords = null,
    int? MaxWords = null,
    IReadOnlyList<string>? MustContain = null,
    IReadOnlyList<string>? MustNotContain = null,
    string? Format = null)
{
    public const string FormatJson = "json";
    public const string FormatMarkdownList = "markdownList";
    public const string FormatNoMarkdown = "noMarkdown";
}

/// <summary>A <c>pythonTests</c> criterion's configuration.</summary>
/// <remarks>
///     <c>exports</c> names the symbols the operator's tests call directly (<c>solve(10)</c>); omitted, the tests
///     reach the candidate through the <c>candidate.…</c> proxy, or through <c>pycall</c> / <c>pyeval</c>, which are
///     always in the test namespace and need no flag of their own.
/// </remarks>
public sealed record BenchmarkPythonTestsConfigV1(
    string? TestCode = null,
    IReadOnlyList<string>? Exports = null,
    int? TimeoutSeconds = null,
    string? Extract = null);

/// <summary>One criterion's verifiable configuration, parsed and validated once.</summary>
/// <remarks>
///     Produced by <see cref="BenchmarkJudgeVerifierConfig.Parse" />, which BOTH the policy validator (at activation,
///     discarding the result) and <see cref="BenchmarkJudgeVerifiers" /> (at execution) call — a second parser is how
///     an activation-time check and a run-time check drift into disagreeing about the same config.
/// </remarks>
public sealed record BenchmarkVerifierSpec
{
    public required string Kind { get; init; }
    public string? ExpectedText { get; init; }
    public BenchmarkVerifierNormalizeV1 Normalize { get; init; } = new();
    public Regex? Pattern { get; init; }
    public bool MustMatch { get; init; } = true;
    public JsonElement Schema { get; init; }
    public double ExpectedNumber { get; init; }
    public double RelativeTolerance { get; init; }
    public double AbsoluteTolerance { get; init; }
    public BenchmarkConstraintConfigV1? Constraint { get; init; }
    public BenchmarkPythonTestsConfigV1? PythonTests { get; init; }
}
