namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>What a training-stepper run must show before anything is saved: a clean script run, then outputs of the right size, version, template and shape.</summary>
/// <remarks>
///     Structure only: the host never reads a loss or a gradient, which are the script's (ADR 0005). The trace is checked down to every cell's kind,
///     so a saved run is one the stepper can play and its save never trims a playable one for it.
/// </remarks>
internal static class ModelLabPlaygroundChecks
{
    /// <summary>JavaScript's <c>Number.MAX_SAFE_INTEGER</c>, the top of what <c>z.int()</c> accepts.</summary>
    private const double MaxSafeInteger = 9007199254740991;

    private static readonly Dictionary<string, JsonValueKind> TraceMembers = new(StringComparer.Ordinal)
    {
        ["torchVersion"] = JsonValueKind.String,
        ["device"] = JsonValueKind.String,
        ["dtype"] = JsonValueKind.String,
        ["threads"] = JsonValueKind.Number,
        ["config"] = JsonValueKind.Object,
        ["stepDefinition"] = JsonValueKind.Object,
        ["data"] = JsonValueKind.Object,
        ["modules"] = JsonValueKind.Array,
        ["parameters"] = JsonValueKind.Array,
        ["occurrences"] = JsonValueKind.Array,
        ["recorded"] = JsonValueKind.Array
    };

    /// <summary>Every knob the summary reports, since a missing one would read back as a default the run never used. The counts are int32, as the API's schema has them.</summary>
    private static readonly Dictionary<string, Func<JsonElement, bool>> ConfigMembers = new(StringComparer.Ordinal)
    {
        ["dataset"] = IsString,
        ["learningRate"] = IsNumber,
        ["residual"] = IsBoolean,
        ["sharedBlock"] = IsBoolean,
        ["depth"] = IsInt32,
        ["hidden"] = IsInt32,
        ["steps"] = IsInt32,
        ["seed"] = IsInt32
    };

    // What the React stepper's trace schema requires below the top level: JSON kinds, and an index is a non-negative integer. A member whose check accepts an absent value is optional.
    private static readonly Dictionary<string, Func<JsonElement, bool>> DataMembers = new(StringComparer.Ordinal)
    {
        ["count"] = IsIndex,
        ["probes"] = IsIndexList,
        ["x"] = IsMatrix,
        ["y"] = IsRow
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> ModuleMembers = new(StringComparer.Ordinal)
    {
        ["path"] = IsString,
        ["class"] = IsString,
        ["outputActivation"] = static value => value.ValueKind is JsonValueKind.String or JsonValueKind.Null
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> ParameterMembers = new(StringComparer.Ordinal)
    {
        ["id"] = IsString,
        ["module"] = IsString,
        ["shape"] = IsIndexList
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> OccurrenceMembers = new(StringComparer.Ordinal)
    {
        ["module"] = IsString,
        ["visit"] = IsIndex
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> ForwardMembers = new(StringComparer.Ordinal)
    {
        ["o"] = IsIndex,
        ["input"] = IsMatrix,
        ["output"] = IsMatrix,
        ["stream"] = static value => value.ValueKind == JsonValueKind.Undefined || IsMatrix(value)
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> LossMembers = new(StringComparer.Ordinal)
    {
        ["value"] = IsCell,
        ["probeLogits"] = IsRow,
        ["accuracy"] = IsCell
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> OutputGradMembers = new(StringComparer.Ordinal)
    {
        ["o"] = IsIndex,
        ["grad"] = IsMatrix
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> ParameterGradMembers = new(StringComparer.Ordinal)
    {
        ["p"] = IsIndex,
        ["grad"] = IsTensor
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> VisitGradMembers = new(StringComparer.Ordinal)
    {
        ["o"] = IsIndex,
        ["p"] = IsIndex,
        ["grad"] = IsTensor
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> BackwardMembers = new(StringComparer.Ordinal)
    {
        ["outputGrads"] = static value => IsListOf(value, OutputGradMembers),
        ["parameterGrads"] = static value => IsListOf(value, ParameterGradMembers),
        ["visitGrads"] = static value => IsListOf(value, VisitGradMembers)
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> UpdateMembers = new(StringComparer.Ordinal)
    {
        ["p"] = IsIndex,
        ["before"] = IsTensor,
        ["after"] = IsTensor
    };

    private static readonly Dictionary<string, Func<JsonElement, bool>> RecordedMembers = new(StringComparer.Ordinal)
    {
        ["step"] = IsIndex,
        ["forward"] = static value => IsListOf(value, ForwardMembers),
        ["loss"] = static value => MissingMemberOrNull(value, LossMembers) is null,
        ["backward"] = static value => value.ValueKind == JsonValueKind.Null || MissingMemberOrNull(value, BackwardMembers) is null,
        ["update"] = static value => value.ValueKind == JsonValueKind.Null || IsListOf(value, UpdateMembers)
    };

    /// <summary>The top-level arrays playback walks, each with what every one of its items must hold. Declared after the item shapes it names.</summary>
    private static readonly Dictionary<string, Dictionary<string, Func<JsonElement, bool>>> PlaybackLists = new(StringComparer.Ordinal)
    {
        ["modules"] = ModuleMembers,
        ["parameters"] = ParameterMembers,
        ["occurrences"] = OccurrenceMembers,
        ["recorded"] = RecordedMembers
    };

    /// <summary>Why the script run did not do its work, or null when it did.</summary>
    public static string? RunFailureOrNull(ModelLabScriptRunner.ScriptRun run, bool cancelRequested)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (cancelRequested || run.State.ScriptCancelled || run.ExitCode == TrainingRunExecutor.CancelledExitCode)
        {
            return "The run was stopped before it finished, so nothing was saved.";
        }

        if (run.FailureReason is { } reason)
        {
            return reason;
        }

        if (run.State.ErrorMessage is { Length: > 0 } error)
        {
            return run.State.WithLastOutput(error);
        }

        return !run.State.HandshakeSeen || !run.State.Done || run.ExitCode != 0
            ? run.State.WithLastOutput($"The training stepper script exited with status {run.ExitCode}.")
            : null;
    }

    /// <summary>Why an output file cannot be read as written, or null: it is missing or over its cap.</summary>
    public static string? SizeProblemOrNull(FileInfo file, int maxBytes, string name)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.Exists)
        {
            return $"The training stepper script finished without writing its {name}.";
        }

        return file.Length > maxBytes ? $"The {name} is larger than its {maxBytes}-byte cap, so nothing was saved." : null;
    }

    public static string? TraceProblemOrNull(ReadOnlyMemory<byte> json) =>
        Problem(json, "trace", static root =>
        {
            foreach (var (name, kind) in TraceMembers)
            {
                if (!Has(root, name, kind))
                {
                    return name;
                }
            }

            return ConfigProblemOrNull(root) ?? OutcomeProblemOrNull(root) ?? PlaybackProblemOrNull(root);
        });

    public static string? ResultProblemOrNull(ReadOnlyMemory<byte> json) =>
        Problem(json, "summary", static root =>
        {
            if (ConfigProblemOrNull(root) is { } config)
            {
                return config;
            }

            if (OutcomeProblemOrNull(root) is { } outcome)
            {
                return outcome;
            }

            if (!IsNumberOrNull(root, "finalLoss"))
            {
                return "finalLoss";
            }

            return IsNumberOrNull(root, "finalAccuracy") ? null : "finalAccuracy";
        });

    /// <param name="missingMember">The first structural member that is missing or of the wrong kind, or null.</param>
    private static string? Problem(ReadOnlyMemory<byte> json, string name, Func<JsonElement, string?> missingMember)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("formatVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var format)
                || format != ModelLabPlaygroundSpec.FormatVersion)
            {
                return $"The training stepper script wrote a {name} in a format this build does not read.";
            }

            if (!root.TryGetProperty("template", out var template) || template.ValueKind != JsonValueKind.String
                                                                   || template.GetString() != ModelLabPlaygroundSpec.TemplateId)
            {
                return $"The training stepper script wrote a {name} for another template.";
            }

            return missingMember(root) is { } missing ? $"The training stepper script wrote a {name} without its '{missing}'." : null;
        }
        catch (JsonException)
        {
            return $"The training stepper script wrote a {name} that is not valid JSON.";
        }
    }

    private static string? ConfigProblemOrNull(JsonElement root)
    {
        if (!root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            return "config";
        }

        return MissingMemberOrNull(config, ConfigMembers) is { } member ? "config." + member : null;
    }

    /// <summary>The first nested member the stepper's playback needs that is missing or of the wrong kind, named by its path. Values are never read.</summary>
    private static string? PlaybackProblemOrNull(JsonElement root)
    {
        if (MissingMemberOrNull(root.GetProperty("data"), DataMembers) is { } data)
        {
            return "data." + data;
        }

        foreach (var (list, members) in PlaybackLists)
        {
            var index = 0;
            foreach (var item in root.GetProperty(list).EnumerateArray())
            {
                if (MissingMemberOrNull(item, members) is { } member)
                {
                    return $"{list}[{index}].{member}";
                }

                index++;
            }
        }

        return null;
    }

    /// <summary>The first of <paramref name="members" /> that <paramref name="element" /> lacks or holds with the wrong kind; any member when it is not an object.</summary>
    private static string? MissingMemberOrNull(JsonElement element, Dictionary<string, Func<JsonElement, bool>> members)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return members.Keys.First();
        }

        foreach (var (name, isValid) in members)
        {
            // An absent member is checked as an undefined value, so only an optional one passes.
            if (!isValid(element.TryGetProperty(name, out var value) ? value : default))
            {
                return name;
            }
        }

        return null;
    }

    private static bool IsListOf(JsonElement element, Dictionary<string, Func<JsonElement, bool>> members) =>
        element.ValueKind == JsonValueKind.Array && element.EnumerateArray().All(item => MissingMemberOrNull(item, members) is null);

    private static bool IsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String;

    private static bool IsNumber(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number;

    private static bool IsBoolean(JsonElement element) =>
        element.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out _);

    /// <summary>What the stepper's <c>z.int().nonnegative()</c> takes: a whole number from 0 to 2^53 - 1, however it is written.</summary>
    private static bool IsIndex(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && value is >= 0 and <= MaxSafeInteger && double.IsInteger(value);

    private static bool IsIndexList(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array && element.EnumerateArray().All(IsIndex);

    /// <summary>A recorded number: the script writes a non-finite one as null.</summary>
    private static bool IsCell(JsonElement element) =>
        element.ValueKind is JsonValueKind.Number or JsonValueKind.Null;

    private static bool IsRow(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array && element.EnumerateArray().All(IsCell);

    private static bool IsMatrix(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array && element.EnumerateArray().All(IsRow);

    /// <summary>A parameter or its gradient: a bias is one row, a weight a matrix.</summary>
    private static bool IsTensor(JsonElement element) =>
        IsMatrix(element) || IsRow(element);

    private static string? OutcomeProblemOrNull(JsonElement root)
    {
        if (!root.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.Object)
        {
            return "outcome";
        }

        var kind = outcome.TryGetProperty("kind", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (kind is not ("finished" or "diverged"))
        {
            return "outcome.kind";
        }

        if (!outcome.TryGetProperty("atStep", out var atStep) || (atStep.ValueKind != JsonValueKind.Null && !IsIndex(atStep)))
        {
            return "outcome.atStep";
        }

        return outcome.TryGetProperty("updatesApplied", out var applied) && IsIndex(applied) ? null : "outcome.updatesApplied";
    }

    private static bool Has(JsonElement element, string name, JsonValueKind kind) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == kind;

    private static bool IsNumberOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.Null;
}
