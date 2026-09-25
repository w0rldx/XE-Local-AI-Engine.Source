namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Reads and writes the persisted <see cref="ConversationStateDocument" /> blob.</summary>
public static class ConversationStateSerializer
{
    // Same settings as the summarizer: the blob is stored encrypted and never rendered, so relaxed escaping is safe.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public static string Serialize(ConversationStateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    /// <summary>
    ///     Returns the document, or null for a missing, unparseable or other-version blob; callers treat null as
    ///     "no state yet", so a corrupt blob never fails a turn.
    /// </summary>
    public static ConversationStateDocument? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ConversationStateDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ConversationStateDocument>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        // JSON nulls bypass the non-nullable annotations; a document carrying one is as corrupt as unparseable text.
        return document is { Version: ConversationStateDocument.CurrentVersion, Entries: not null }
               && document.Entries.All(static entry => entry is { Id: not null, Value: not null, SourceSequences: not null })
            ? document
            : null;
    }
}
