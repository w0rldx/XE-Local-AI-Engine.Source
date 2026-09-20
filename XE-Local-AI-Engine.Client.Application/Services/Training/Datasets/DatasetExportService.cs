namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

public interface IDatasetExportService
{
    Task<string> ExportAsync(Guid datasetId, DatasetExportFormat format, CancellationToken cancellationToken = default);
}

/// <summary>
///     Dataset export. The canonical form is template-agnostic on purpose: the base model's chat template is applied
///     inside the trainer, never here.
/// </summary>
/// <remarks>
///     Rejected samples are excluded — a rejection is an operator's decision that the sample must not train
///     anything. ponytail: the whole export is built in memory. A definition is capped at 2000 samples, so the
///     ceiling is a few MB; stream it if that cap is ever raised.
/// </remarks>
public sealed class DatasetExportService : IDatasetExportService
{
    private const string ToolCallOpen = "<tool_call>";
    private const string ToolCallClose = "</tool_call>";

    private readonly ITrainingDatasetStore _store;

    public DatasetExportService(ITrainingDatasetStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<string> ExportAsync(Guid datasetId, DatasetExportFormat format, CancellationToken cancellationToken = default)
    {
        _ = await _store.GetDatasetAsync(datasetId, cancellationToken)
            ?? throw new TrainingNotFoundException("The training dataset was not found.");
        var samples = await _store.ListAllSamplesAsync(datasetId, cancellationToken);
        var builder = new StringBuilder();
        foreach (var sample in samples.Where(item => item.ReviewState != TrainingSampleReviewState.Rejected))
        {
            var content = JsonSerializer.Deserialize<TrainingSampleContentV1>(sample.ContentJson.Span, TrainingJson.Options);
            if (content is null)
            {
                continue;
            }

            _ = builder.AppendLine(format == DatasetExportFormat.Hermes ? ToHermesLine(content) : ToCanonicalLine(sample, content));
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Reads a Hermes <c>&lt;tool_call&gt;</c> block back into its tool name and argument JSON. Public so the exported
    ///     form is verifiable by parsing it rather than by string-matching the writer's own output.
    /// </summary>
    public static bool TryReadHermesToolCall(string value, out string toolName, out string argumentsJson)
    {
        toolName = string.Empty;
        argumentsJson = string.Empty;
        var start = value.IndexOf(ToolCallOpen, StringComparison.Ordinal);
        var end = value.IndexOf(ToolCallClose, StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            return false;
        }

        var payload = value[(start + ToolCallOpen.Length)..end].Trim();
        try
        {
            var node = JsonNode.Parse(payload);
            if (node?["name"]?.GetValue<string>() is not { } name)
            {
                return false;
            }

            toolName = name;
            argumentsJson = node["arguments"]?.ToJsonString() ?? "{}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ToCanonicalLine(TrainingSampleRecord sample, TrainingSampleContentV1 content) =>
        FrozenTrainingCorpus.WriteLine(sample, content);

    private static string ToHermesLine(TrainingSampleContentV1 content)
    {
        var conversations = new List<object>();
        if (!string.IsNullOrWhiteSpace(content.SystemInstructions))
        {
            conversations.Add(new
            {
                from = "system",
                value = content.SystemInstructions
            });
        }

        foreach (var part in content.Parts)
        {
            switch (part.Kind)
            {
                case "user":
                    conversations.Add(new
                    {
                        from = "human",
                        value = part.Content ?? string.Empty
                    });
                    break;
                case "tool":
                    conversations.Add(new
                    {
                        from = "gpt",
                        value = $"{ToolCallOpen}\n{ToolCallJson(part)}\n{ToolCallClose}"
                    });
                    conversations.Add(new
                    {
                        from = "tool",
                        value = part.Result ?? string.Empty
                    });
                    break;
                default:
                    conversations.Add(new
                    {
                        from = "gpt",
                        value = part.Content ?? string.Empty
                    });
                    break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            conversations
        }, TrainingJson.Options);
    }

    private static string ToolCallJson(TrainingSamplePartV1 part)
    {
        // Arguments travel as a JSON object inside the call, not as an escaped string: that is what Hermes consumers
        // parse. A body that will not parse degrades to an empty object rather than corrupting the whole line.
        JsonNode? arguments = null;
        if (!string.IsNullOrWhiteSpace(part.Arguments))
        {
            try
            {
                arguments = JsonNode.Parse(part.Arguments);
            }
            catch (JsonException)
            {
                arguments = null;
            }
        }

        return new JsonObject
        {
            ["name"] = part.ToolName ?? string.Empty,
            ["arguments"] = arguments ?? new JsonObject()
        }.ToJsonString();
    }
}
