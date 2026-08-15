namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Text.Json;

/// <summary>Every event <c>train.py</c> can emit on stdout. Contract version 1.</summary>
public enum TrainingStdioEventKind
{
    Handshake,
    Phase,
    Progress,
    Heartbeat,
    Artifact,
    Done,
    Error
}

/// <summary>
///     One parsed stdio line. Fields are shared across kinds rather than split into a type per event — the protocol is
///     seven flat messages, and a hierarchy would cost more than it explains.
/// </summary>
public sealed record TrainingStdioEvent(
    TrainingStdioEventKind Kind,
    int? ContractVersion = null,
    string? Phase = null,
    int? Step = null,
    int? TotalSteps = null,
    double? Epoch = null,
    double? Loss = null,
    double? LearningRate = null,
    long? VramBytes = null,
    string? ArtifactKind = null,
    string? Path = null,
    string? Category = null,
    string? Message = null,
    bool Cancelled = false);

/// <summary>
///     Parses the trainer's JSON-lines protocol out of a stream that is NOT clean.
/// </summary>
/// <remarks>
///     Importing unsloth prints banner lines before the script writes anything, torch warns to stderr at will, and both
///     streams are merged — so the parser scans for lines that parse as a JSON object carrying a known <c>event</c>,
///     and treats everything else as log-tail text. This is the same decision <c>TrainingRuntimeProbeParser</c> made
///     after the probe's banners broke a first-line read in live verification.
/// </remarks>
public static class TrainingRunStdioParser
{
    public const int ContractVersion = 1;

    /// <summary>Returns the parsed event, or null when the line is banner text rather than protocol.</summary>
    public static TrainingStdioEvent? TryParse(string? line)
    {
        var trimmed = line?.Trim();
        if (trimmed is not { Length: > 0 } || trimmed[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return name.GetString() switch
            {
                "handshake" => new TrainingStdioEvent(TrainingStdioEventKind.Handshake, ReadInt(root, "contractVersion")),
                "phase" => new TrainingStdioEvent(TrainingStdioEventKind.Phase, Phase: ReadString(root, "phase")),
                "progress" => new TrainingStdioEvent(TrainingStdioEventKind.Progress,
                    Step: ReadInt(root, "step"),
                    TotalSteps: ReadInt(root, "totalSteps"),
                    Epoch: ReadDouble(root, "epoch"),
                    Loss: ReadDouble(root, "loss"),
                    LearningRate: ReadDouble(root, "lr"),
                    VramBytes: ReadLong(root, "vramBytes")),
                "heartbeat" => new TrainingStdioEvent(TrainingStdioEventKind.Heartbeat, Phase: ReadString(root, "phase")),
                "artifact" => new TrainingStdioEvent(TrainingStdioEventKind.Artifact,
                    ArtifactKind: ReadString(root, "kind"),
                    Path: ReadString(root, "path")),
                "done" => new TrainingStdioEvent(TrainingStdioEventKind.Done, Cancelled: ReadBool(root, "cancelled")),
                "error" => new TrainingStdioEvent(TrainingStdioEventKind.Error,
                    Category: ReadString(root, "category"),
                    Message: ReadString(root, "message")),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
