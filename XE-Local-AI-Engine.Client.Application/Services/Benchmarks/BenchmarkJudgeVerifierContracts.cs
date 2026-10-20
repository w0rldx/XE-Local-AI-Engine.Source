namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>A <c>sceneSpec</c> criterion's configuration. Every member is optional: <c>{}</c> means "a valid v1 scene".</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkSceneSpecConfigV1
{
    public const string RelationAbove = "above";
    public const string RelationLeftOf = "leftOf";
    public const string RelationInside = "inside";

    public BenchmarkSceneObjectCountV1? ObjectCount { get; init; }
    public IReadOnlyList<string>? RequiresPrimitive { get; init; }
    public IReadOnlyList<BenchmarkSceneRelationV1>? Relations { get; init; }
}

/// <summary>Inclusive bounds on how many objects a scene holds.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkSceneObjectCountV1
{
    public int? Min { get; init; }
    public int? Max { get; init; }
}

/// <summary>A spatial relation between two scene objects, named by id, decided on their axis-aligned bounds.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkSceneRelationV1
{
    public string? Kind { get; init; }
    public string? A { get; init; }
    public string? B { get; init; }
}

/// <summary>A point or extent in scene space. Y is up.</summary>
public sealed record BenchmarkSceneVector
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Z { get; init; }
}

/// <summary>An axis-aligned box in scene space.</summary>
public sealed record BenchmarkSceneBounds
{
    public required BenchmarkSceneVector Min { get; init; }
    public required BenchmarkSceneVector Max { get; init; }
}

/// <summary>One object of a parsed v1 scene, with the optional members already defaulted.</summary>
public sealed record BenchmarkSceneObject
{
    public required string Id { get; init; }
    public required string Primitive { get; init; }
    public required BenchmarkSceneVector Position { get; init; }
    public required BenchmarkSceneVector Rotation { get; init; }
    public required BenchmarkSceneVector Scale { get; init; }
    public required string Color { get; init; }
}

/// <summary>A scene the model answered with, accepted by <see cref="BenchmarkSceneSpec.TryParse" />.</summary>
public sealed record BenchmarkScene
{
    public required IReadOnlyList<BenchmarkSceneObject> Objects { get; init; }
}

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
    public BenchmarkSceneSpecConfigV1? SceneSpec { get; init; }
}
