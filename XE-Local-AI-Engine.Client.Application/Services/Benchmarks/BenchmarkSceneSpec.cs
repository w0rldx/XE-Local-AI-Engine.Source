namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

/// <summary>
///     Scene schema v1: reads a model's scene answer and decides spatial relations on axis-aligned bounds. Pure and
///     closed: a member the schema does not name is refused, never ignored.
/// </summary>
/// <remarks>
///     The run pane's viewer mirrors these rules for display only. This class is what a <c>sceneSpec</c> score is
///     decided by, so a refusal here is the detail the operator reads.
/// </remarks>
public static class BenchmarkSceneSpec
{
    public const int SchemaVersion = 1;
    public const int MaximumObjects = 64;
    public const int MaximumIdLength = 64;
    public const double PositionLimit = 1000;
    public const double RotationLimit = 360;
    public const double ScaleLimit = 1000;
    public const string DefaultColor = "#808080";

    public const string Box = "box";
    public const string Sphere = "sphere";
    public const string Cylinder = "cylinder";
    public const string Cone = "cone";
    public const string Plane = "plane";

    /// <summary>Every primitive a v1 scene may name, matched exactly and case-sensitively.</summary>
    public static readonly IReadOnlyList<string> Primitives = [Box, Sphere, Cylinder, Cone, Plane];

    private static readonly string[] RequiredMembers = ["id", "primitive", "position"];

    private static readonly BenchmarkSceneVector Zero = new()
    {
        X = 0,
        Y = 0,
        Z = 0
    };

    private static readonly BenchmarkSceneVector One = new()
    {
        X = 1,
        Y = 1,
        Z = 1
    };

    /// <summary>
    ///     The scene <paramref name="json" /> describes, or <see langword="false" /> with the first schema violation as
    ///     <paramref name="reason" />. Never throws for a malformed answer: that is a failed criterion, not a fault.
    /// </summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out BenchmarkScene? scene, out string reason)
    {
        ArgumentNullException.ThrowIfNull(json);
        scene = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            reason = "The answer is not valid JSON.";
            return false;
        }

        using (document)
        {
            var objects = new List<BenchmarkSceneObject>();
            reason = ReadScene(document.RootElement, objects) ?? string.Empty;
            if (reason.Length > 0)
            {
                return false;
            }

            scene = new BenchmarkScene
            {
                Objects = objects
            };
            return true;
        }
    }

    /// <summary>The object's box: <c>position ± scale / 2</c> on each axis of its unit primitive; a plane has no thickness.</summary>
    public static BenchmarkSceneBounds Bounds(BenchmarkSceneObject sceneObject)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);

        // Accepted ceiling: rotation is ignored, so a rotated object is checked by its unrotated box.
        // A rotation-aware oriented-box test is the upgrade if an operator needs one.
        var halfX = sceneObject.Scale.X / 2;
        var halfY = string.Equals(sceneObject.Primitive, Plane, StringComparison.Ordinal) ? 0 : sceneObject.Scale.Y / 2;
        var halfZ = sceneObject.Scale.Z / 2;
        var position = sceneObject.Position;
        return new BenchmarkSceneBounds
        {
            Min = new BenchmarkSceneVector
            {
                X = position.X - halfX,
                Y = position.Y - halfY,
                Z = position.Z - halfZ
            },
            Max = new BenchmarkSceneVector
            {
                X = position.X + halfX,
                Y = position.Y + halfY,
                Z = position.Z + halfZ
            }
        };
    }

    /// <summary>Whether <paramref name="relation" /> (<c>above</c>, <c>leftOf</c> or <c>inside</c>) holds from a to b.</summary>
    public static bool Holds(string relation, BenchmarkSceneObject a, BenchmarkSceneObject b)
    {
        var first = Bounds(a);
        var second = Bounds(b);
        return relation switch
        {
            BenchmarkSceneSpecConfigV1.RelationAbove => first.Min.Y >= second.Max.Y,
            BenchmarkSceneSpecConfigV1.RelationLeftOf => first.Max.X <= second.Min.X,
            BenchmarkSceneSpecConfigV1.RelationInside => first.Min.X >= second.Min.X && first.Max.X <= second.Max.X
                                                                                     && first.Min.Y >= second.Min.Y && first.Max.Y <= second.Max.Y
                                                                                     && first.Min.Z >= second.Min.Z && first.Max.Z <= second.Max.Z,
            _ => throw new UnreachableException(relation)
        };
    }

    private static string? ReadScene(JsonElement root, List<BenchmarkSceneObject> objects)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "The scene must be a JSON object.";
        }

        JsonElement? version = null;
        JsonElement? items = null;
        foreach (var member in root.EnumerateObject())
        {
            switch (member.Name)
            {
                case "version" when version is null:
                    version = member.Value;
                    break;
                case "objects" when items is null:
                    items = member.Value;
                    break;
                default:
                    return $"The scene carries the unknown or repeated member '{member.Name}'.";
            }
        }

        if (version is not { ValueKind: JsonValueKind.Number } versionValue
            || !versionValue.TryGetInt32(out var number) || number != SchemaVersion)
        {
            return $"The scene version must be {SchemaVersion}.";
        }

        if (items is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() is < 1 or > MaximumObjects)
        {
            return $"The scene objects must be an array of 1 to {MaximumObjects} objects.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            index++;
            if (ReadObject(item, $"object {index}") is { } failure)
            {
                return failure;
            }

            var sceneObject = ToObject(item);
            if (!ids.Add(sceneObject.Id))
            {
                return $"object {index}: the id '{sceneObject.Id}' is already used.";
            }

            objects.Add(sceneObject);
        }

        return null;
    }

    /// <summary>The first violation in one object, or <see langword="null" />. Validation only; <see cref="ToObject" /> reads it after.</summary>
    private static string? ReadObject(JsonElement item, string label)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return $"{label} must be a JSON object.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in item.EnumerateObject())
        {
            if (!seen.Add(member.Name))
            {
                return $"{label}: the member '{member.Name}' is repeated.";
            }

            var failure = member.Name switch
            {
                "id" => member.Value.ValueKind == JsonValueKind.String && member.Value.GetString() is { Length: > 0 and <= MaximumIdLength }
                    ? null
                    : $"{label}: 'id' must be a non-empty string of at most {MaximumIdLength} characters.",
                "primitive" => member.Value.ValueKind == JsonValueKind.String
                               && Primitives.Contains(member.Value.GetString(), StringComparer.Ordinal)
                    ? null
                    : $"{label}: 'primitive' must be one of {string.Join(", ", Primitives)}.",
                "position" => IsVector(member.Value, static value => value is >= -PositionLimit and <= PositionLimit)
                    ? null
                    : $"{label}: 'position' must be three numbers in [-1000, 1000].",
                "rotation" => IsVector(member.Value, static value => value is >= -RotationLimit and <= RotationLimit)
                    ? null
                    : $"{label}: 'rotation' must be three numbers in [-360, 360].",
                "scale" => IsVector(member.Value, static value => value is > 0 and <= ScaleLimit)
                    ? null
                    : $"{label}: 'scale' must be three numbers in (0, 1000].",
                "color" => member.Value.ValueKind == JsonValueKind.String && IsHexColor(member.Value.GetString())
                    ? null
                    : $"{label}: 'color' must be #rrggbb.",
                _ => $"{label}: unknown member '{member.Name}'."
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return RequiredMembers.FirstOrDefault(name => !seen.Contains(name)) is { } missing
            ? $"{label} is missing '{missing}'."
            : null;
    }

    private static BenchmarkSceneObject ToObject(JsonElement item) =>
        new()
        {
            Id = item.GetProperty("id").GetString()!,
            Primitive = item.GetProperty("primitive").GetString()!,
            Position = ToVector(item.GetProperty("position")),
            Rotation = item.TryGetProperty("rotation", out var rotation) ? ToVector(rotation) : Zero,
            Scale = item.TryGetProperty("scale", out var scale) ? ToVector(scale) : One,
            Color = item.TryGetProperty("color", out var color) ? color.GetString()! : DefaultColor
        };

    private static bool IsVector(JsonElement value, Func<double, bool> inRange) =>
        value.ValueKind == JsonValueKind.Array
        && value.GetArrayLength() == 3
        && value.EnumerateArray().All(component => component.ValueKind == JsonValueKind.Number
                                                   && component.TryGetDouble(out var number)
                                                   && double.IsFinite(number)
                                                   && inRange(number));

    private static BenchmarkSceneVector ToVector(JsonElement value) =>
        new()
        {
            X = value[0].GetDouble(),
            Y = value[1].GetDouble(),
            Z = value[2].GetDouble()
        };

    private static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);
}
