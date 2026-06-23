namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Text;
using System.Text.Json;

/// <summary>
///     Shared serialization helpers for the node chat raw-ADO persistence path: content byte encoding, the
///     <c>metadata_json</c> blob, and the conversation <c>selected_path_json</c> map. Pure functions; all node chat
///     persistence collaborators consume these via <c>using static</c>.
/// </summary>
internal static class NodeChatMetadataSerializer
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions SelectedPathJsonOptions = new(JsonSerializerDefaults.Web);

    internal static byte[] Encode(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    internal static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }

    internal static string ResolveNextContent(string currentContent, string? content, bool replaceContent)
    {
        if (content is null)
        {
            return currentContent;
        }

        return replaceContent ? content : currentContent + content;
    }

    internal static string? Preview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }

    internal static byte[]? SerializeMetadata(string? metadataJson,
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

        return Encode(JsonSerializer.Serialize(new NodeChatMessageMetadata(metadataJson, reasoning, model, inputTokens, outputTokens, totalTokens, reasoningTokens, parts, agentDefinitionId, agentName,
                reasoningEffort,
                generationDurationMs),
            MetadataJsonOptions));
    }

    internal static string? SerializeSelectedPath(IReadOnlyDictionary<Guid, Guid> selectedPath)
    {
        if (selectedPath.Count == 0)
        {
            return null;
        }

        // String keys/values keep the JSON object portable: the same {variantGroupId->selectedMessageId} map can be
        // parsed by any platform without depending on a Guid dictionary-key converter.
        var serializable = selectedPath.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToString(), StringComparer.Ordinal);
        return JsonSerializer.Serialize(serializable, SelectedPathJsonOptions);
    }

    internal static IReadOnlyDictionary<Guid, Guid>? DeserializeSelectedPath(string? selectedPathJson)
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

    internal static NodeChatMessageMetadata DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return new NodeChatMessageMetadata(MetadataJson: null, Reasoning: null, Model: null, InputCount: null, OutputCount: null, TotalCount: null, ReasoningCount: null);
        }

        return JsonSerializer.Deserialize<NodeChatMessageMetadata>(metadataJson, MetadataJsonOptions) ??
               new NodeChatMessageMetadata(metadataJson, Reasoning: null, Model: null, InputCount: null, OutputCount: null, TotalCount: null, ReasoningCount: null);
    }
}
