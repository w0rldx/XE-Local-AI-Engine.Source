namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>How the teacher is asked to produce a structured sample (plan decision #15).</summary>
public enum TeacherOutputMode
{
    /// <summary>Constrained decoding via <c>response_format</c>. Reasoning-mode teachers are refused — they bypass it.</summary>
    Constrained,

    /// <summary>No response format; the raw completion is parsed and schema-validated post-hoc. Reasoning teachers allowed.</summary>
    ValidateAfter
}

/// <summary>
///     The definition body persisted (encrypted) in <see cref="TrainingDatasetDefinition.DefinitionJson" />. The tool
///     schema snapshot is taken at save time so a later catalog change can never silently re-shape an existing dataset.
/// </summary>
public sealed record DatasetDefinitionBodyV1
{
    public const double MinHoldoutFraction = 0.05;
    public const double MaxHoldoutFraction = 0.30;
    public const double DefaultHoldoutFraction = 0.10;

    public int SchemaVersion { get; init; } = 1;

    public string? Description { get; init; }

    /// <summary>Node-local teacher model. Cloud models are refused by the definition service.</summary>
    public string TeacherModelName { get; init; } = string.Empty;

    public TeacherOutputMode TeacherOutputMode { get; init; }

    public string SystemInstructions { get; init; } = string.Empty;

    /// <summary>The tool names the teacher is offered, snapshotted with their composed approval below.</summary>
    public IReadOnlyList<DatasetToolSnapshotV1> Tools { get; init; } = [];

    public IReadOnlyList<DatasetSampleKindTargetV1> SampleKinds { get; init; } = [];

    public double HoldoutFraction { get; init; } = DefaultHoldoutFraction;

    public float Temperature { get; init; }

    /// <summary>
    ///     Base seed, carried as a string for the same reason <c>SamplingOptions.Seed</c> is: a seed is an unconstrained
    ///     64-bit value and a JSON number loses precision above 2^53. Null means "no seed policy".
    /// </summary>
    public string? BaseSeed { get; init; }

    public bool CriticEnabled { get; init; }

    public string? CriticModelName { get; init; }
}

/// <summary>
///     A tool as it looked in the catalog when the definition was saved. <see cref="RequiresApproval" /> is the COMPOSED
///     effective flag (catalog default tightened by <c>IToolApprovalPolicy</c>), not the raw catalog default.
/// </summary>
public sealed record DatasetToolSnapshotV1(
    string Name,
    string? Description,
    string? ParameterSchema,
    bool RequiresApproval,
    ToolCategory Category);

/// <summary>How many samples of a given kind/label the generation run should aim for.</summary>
public sealed record DatasetSampleKindTargetV1(string Kind, int Count, TrainingSampleLabel Label);

/// <summary>One ordered part of a sample trajectory. Mirrors the chat <c>parts[]</c> wire shape so the existing renderer works.</summary>
public sealed record TrainingSamplePartV1(
    string Kind,
    int Sequence,
    string? Content = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? Arguments = null,
    string? Result = null,
    bool? IsError = null);

/// <summary>The sample trajectory persisted (encrypted) in <see cref="TrainingDatasetSample.ContentJson" />.</summary>
public sealed record TrainingSampleContentV1
{
    public int SchemaVersion { get; init; } = 1;

    public string SystemInstructions { get; init; } = string.Empty;

    public IReadOnlyList<TrainingSamplePartV1> Parts { get; init; } = [];
}

/// <summary>Every validation layer's outcome, persisted in <see cref="TrainingDatasetSample.ValidationJson" /> (invariant #7).</summary>
public sealed record TrainingSampleValidationV1
{
    public int SchemaVersion { get; init; } = 1;

    public bool Passed { get; init; }

    public IReadOnlyList<SampleValidationLayerResultV1> Layers { get; init; } = [];
}

/// <summary>
///     One layer's verdict. <paramref name="ScoredBy" /> carries provenance in the
///     <c>DefaultPlaybookEvalJudge</c> style — "schema", "tool-name", "arguments", "execution", "critic:deterministic",
///     "critic:judge".
/// </summary>
public sealed record SampleValidationLayerResultV1(string Layer, bool Passed, string ScoredBy, string? Reason = null);

/// <summary>Declarative mock body persisted (encrypted) in <see cref="ToolMockDefinition.MockJson" />.</summary>
public sealed record ToolMockBodyV1
{
    public const int MaxRules = 32;
    public const int MaxResponseLength = 8 * 1024;
    public const int MaxValueLength = 512;

    public int SchemaVersion { get; init; } = 1;

    public IReadOnlyList<ToolMockRuleV1> Rules { get; init; } = [];

    /// <summary>Response for a call no rule matched. Null means "no match, no response" — never a real execution.</summary>
    public string? DefaultResponse { get; init; }
}

public enum ToolMockMatchKind
{
    /// <summary>The argument equals <c>Value</c> (ordinal string comparison over the JSON scalar's text).</summary>
    Equality,

    /// <summary>The argument is present and non-null. <c>Value</c>/<c>AnyOf</c> are ignored.</summary>
    Presence,

    /// <summary>The argument equals one of <c>AnyOf</c>.</summary>
    Enum
}

public sealed record ToolMockRuleV1(string Field, ToolMockMatchKind Match, string? Value, IReadOnlyList<string>? AnyOf, string Response);

/// <summary>The static verifier's verdict, persisted in <see cref="ToolMockDefinition.VerificationJson" />.</summary>
public sealed record ToolMockVerificationV1(int SchemaVersion, bool Passed, IReadOnlyList<string> Findings);

/// <summary>The record the teacher is asked to emit — deliberately flat and small: the llama.cpp grammar budget spans the whole request (tools + response schema).</summary>
public sealed record TeacherSampleRecordV1
{
    public string UserMessage { get; init; } = string.Empty;

    public string AssistantText { get; init; } = string.Empty;

    /// <summary>Empty when the sample demonstrates a no-tool answer.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>The tool arguments as a JSON object STRING, not a nested object — nesting would blow the grammar budget.</summary>
    public string ToolArgumentsJson { get; init; } = string.Empty;

    /// <summary>
    ///     Whether this record demonstrates a tool call at all. Live-found (2026-08-15): the MEAI adapter promotes every
    ///     schema property to <c>required</c>, so under constrained decoding the teacher MUST emit some string for
    ///     <see cref="ToolName" /> even for a no-tool answer — and small teachers write "None"/"none"/"None required".
    ///     The no-tool decision therefore lives here, once, instead of every consumer testing for an empty string.
    /// </summary>
    public bool DemonstratesToolCall => !IsNoToolSentinel(ToolName);

    /// <summary>The tool name to resolve, or <see langword="null" /> for a no-tool record.</summary>
    public string? EffectiveToolName => DemonstratesToolCall ? ToolName.Trim() : null;

    private static bool IsNoToolSentinel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        return trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("n/a", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("none ", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("no tool", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Shared serializer settings for every training JSON payload. Web defaults + string enums, matching the wire DTOs.</summary>
public static class TrainingJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
