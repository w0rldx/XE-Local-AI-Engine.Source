namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;

public sealed partial class NodeChatPersistenceService
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions SelectedPathJsonOptions = new(JsonSerializerDefaults.Web);

    private static byte[] Encode(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }

    private static string ResolveNextContent(string currentContent, string? content, bool replaceContent)
    {
        if (content is null)
        {
            return currentContent;
        }

        return replaceContent ? content : currentContent + content;
    }

    private static string? Preview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }

    private static byte[]? SerializeMetadata(string? metadataJson,
        string? reasoning,
        string? model,
        int? inputTokens,
        int? outputTokens,
        int? totalTokens,
        int? reasoningTokens,
        IReadOnlyList<NodeChatMessagePart>? parts = null,
        Guid? agentDefinitionId = null,
        string? agentName = null,
        string? reasoningEffort = null,
        long? generationDurationMs = null)
    {
        if (metadataJson is null && reasoning is null && model is null && inputTokens is null && outputTokens is null && totalTokens is null && reasoningTokens is null && parts is null
            && agentDefinitionId is null && agentName is null && reasoningEffort is null && generationDurationMs is null)
        {
            return null;
        }

        return Encode(JsonSerializer.Serialize(new NodeChatMessageMetadata(metadataJson, reasoning, model, inputTokens, outputTokens, totalTokens, reasoningTokens, parts, agentDefinitionId, agentName, reasoningEffort, generationDurationMs),
            MetadataJsonOptions));
    }

    private static string? SerializeSelectedPath(IReadOnlyDictionary<Guid, Guid> selectedPath)
    {
        if (selectedPath.Count == 0)
        {
            return null;
        }

        // String keys/values keep the JSON object portable: the same {variantGroupId->selectedMessageId} map can be
        // parsed by any platform without depending on a Guid dictionary-key converter.
        var serializable = selectedPath.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToString());
        return JsonSerializer.Serialize(serializable, SelectedPathJsonOptions);
    }

    private static IReadOnlyDictionary<Guid, Guid>? DeserializeSelectedPath(string? selectedPathJson)
    {
        if (string.IsNullOrWhiteSpace(selectedPathJson))
        {
            return null;
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(selectedPathJson, SelectedPathJsonOptions);
        if (raw is null || raw.Count == 0)
        {
            return null;
        }

        var parsed = new Dictionary<Guid, Guid>(raw.Count);
        foreach (var pair in raw)
        {
            if (Guid.TryParse(pair.Key, out var variantGroupId) && Guid.TryParse(pair.Value, out var selectedMessageId))
            {
                parsed[variantGroupId] = selectedMessageId;
            }
        }

        return parsed.Count == 0 ? null : parsed;
    }

    private static NodeChatMessageMetadata DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return new NodeChatMessageMetadata(null, null, null, null, null, null, null);
        }

        return JsonSerializer.Deserialize<NodeChatMessageMetadata>(metadataJson, MetadataJsonOptions) ?? new NodeChatMessageMetadata(metadataJson, null, null, null, null, null, null);
    }

    private static async Task OpenIfNeededAsync(DbConnection? connection, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new InvalidOperationException("The node chat database connection was not available.");
        }

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = DbValue(value);
        command.Parameters.Add(parameter);
    }

    private static object DbValue(object? value)
    {
        return value ?? DBNull.Value;
    }

    private sealed record NodeChatMessageMetadata(
        string? MetadataJson,
        string? Reasoning,
        string? Model,
        int? InputCount,
        int? OutputCount,
        int? TotalCount,
        int? ReasoningCount,
        // Optional ordered interleave. Added after the original metadata shape, so it is the trailing member: a legacy
        // blob written before parts existed omits the key and deserializes with Parts = null (backward-compatible).
        // Stored as plaintext UTF-8 JSON (same posture as Reasoning/Model/token fields on this raw-ADO path —
        // single-user device; see NodeChatPersistenceServiceTests for the documented at-rest posture).
        IReadOnlyList<NodeChatMessagePart>? Parts = null,
        // Per-response agent attribution. Trailing members (after Parts) with null defaults, so a legacy blob written
        // before agent mode existed omits the keys and deserializes with both null (no migration). AgentName is a
        // display-name snapshot — same plaintext-on-device posture as the existing metadata fields.
        Guid? AgentDefinitionId = null,
        string? AgentName = null,
        // The reasoning effort actually used to generate this assistant turn (per-response attribution). Trailing
        // optional member with a null default, so a legacy blob written before this field existed omits the key and
        // deserializes to null (no migration). Same plaintext-on-device posture as the existing metadata fields.
        string? ReasoningEffort = null,
        // Whole-turn wall-clock generation duration in milliseconds (drives the optional tokens-per-second
        // attribution). Trailing optional member with a null default, so a legacy blob written before this field
        // existed omits the key and deserializes to null (no migration). Same plaintext-on-device posture as the
        // existing metadata fields.
        long? GenerationDurationMs = null);
}
