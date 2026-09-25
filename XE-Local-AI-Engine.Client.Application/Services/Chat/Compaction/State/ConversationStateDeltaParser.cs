namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

using System.Text.Json;

/// <summary>
///     Reads the distiller model's delta tolerantly: a code fence or surrounding prose, any property or category
///     casing, and missing arrays are accepted; an item that cannot be read is dropped rather than failing the delta.
/// </summary>
internal static class ConversationStateDeltaParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Returns the delta, or null when the output holds no JSON object with an add, supersede or resolve member.</summary>
    public static ConversationStateDelta? TryParse(string? modelOutput)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
        {
            return null;
        }

        // The outermost braces strip a ```json fence and any prose around the object in one step.
        var start = modelOutput.IndexOf('{', StringComparison.Ordinal);
        var end = modelOutput.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(modelOutput.AsMemory(start, end - start + 1), DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var add = Property(root, "add");
            var supersede = Property(root, "supersede");
            var resolve = Property(root, "resolve");
            if (add is null && supersede is null && resolve is null)
            {
                return null;
            }

            return new ConversationStateDelta
            {
                Add = [.. Items(add).Select(ReadProposed).OfType<ConversationStateProposedEntry>()],
                Supersede = [.. Items(supersede).Select(ReadRetirement).OfType<ConversationStateRetirement>()],
                Resolve = Strings(resolve)
            };
        }
    }

    private static ConversationStateProposedEntry? ReadProposed(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !TryReadCategory(Property(item, "category"), out var category)
            || NonBlank(Property(item, "value")) is not { } value)
        {
            return null;
        }

        return new ConversationStateProposedEntry
        {
            Category = category,
            Value = value,
            SourceSequences = [.. Items(Property(item, "sourceSequences")).Select(ReadInt).OfType<int>()],
            Supersedes = Strings(Property(item, "supersedes"))
        };
    }

    private static ConversationStateRetirement? ReadRetirement(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || NonBlank(Property(item, "entryId")) is not { } entryId
            || Property(item, "bySequence") is not { } sequence
            || ReadInt(sequence) is not { } bySequence)
        {
            return null;
        }

        return new ConversationStateRetirement
        {
            EntryId = entryId,
            BySequence = bySequence
        };
    }

    private static bool TryReadCategory(JsonElement? element, out ConversationStateCategory category)
    {
        category = default;
        if (NonBlank(element) is not { } raw)
        {
            return false;
        }

        // "open_question" and "Open Question" name the same category; a bare number is never a category.
        var normalized = raw.Replace("_", string.Empty, StringComparison.Ordinal)
                            .Replace("-", string.Empty, StringComparison.Ordinal)
                            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.Length > 0
               && char.IsLetter(normalized[0])
               && Enum.TryParse(normalized, ignoreCase: true, out category)
               && Enum.IsDefined(category);
    }

    private static int? ReadInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : null;

    private static string? NonBlank(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string[] Strings(JsonElement? array) =>
        [.. Items(array).Select(static item => NonBlank(item)).OfType<string>()];

    private static IEnumerable<JsonElement> Items(JsonElement? array) =>
        array is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
