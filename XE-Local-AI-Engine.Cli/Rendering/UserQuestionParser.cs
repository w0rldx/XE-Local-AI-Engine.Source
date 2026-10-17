namespace XE_Local_AI_Engine.Cli.Rendering;

using System.Text.Json;
using XE_Local_AI_Engine.Cli.Api;

/// <summary>
///     Reads the <c>question-requested</c> payload the way the SPA's <c>parsePendingUserQuestion</c> does, and builds
///     the answer the node's <c>chat/questions/resolve</c> expects.
/// </summary>
/// <remarks>
///     The payload is the model's raw tool arguments, so this is a trust boundary: a bare array or a
///     <c>{ questions: [...] }</c> envelope is accepted, a question without a usable option is dropped, and anything
///     malformed yields an empty list rather than a half-rendered prompt.
/// </remarks>
internal static class UserQuestionParser
{
    private const int MaxPayloadChars = 256 * 1024;

    public static IReadOnlyList<UserQuestion> Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaxPayloadChars)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var items = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("questions", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array => wrapped,
                _ => default
            };
            return items.ValueKind == JsonValueKind.Array
                ? [.. items.EnumerateArray().Select(ToQuestion).OfType<UserQuestion>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The answer to <paramref name="question" />: the picked option labels plus the free-text row when it was filled.</summary>
    public static QuestionAnswer ToAnswer(UserQuestion question, IEnumerable<string> selectedLabels, string? other)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(selectedLabels);

        var known = question.Options.Select(static option => option.Label).ToHashSet(StringComparer.Ordinal);
        return new QuestionAnswer
        {
            Question = question.Question,
            Selected = [.. selectedLabels.Where(known.Contains).Distinct(StringComparer.Ordinal).Take(question.MultiSelect ? int.MaxValue : 1)],
            Other = string.IsNullOrWhiteSpace(other) ? null : other.Trim()
        };
    }

    private static UserQuestion? ToQuestion(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || ReadString(item, "question") is not { } text)
        {
            return null;
        }

        var options = item.TryGetProperty("options", out var raw) && raw.ValueKind == JsonValueKind.Array
            ? raw.EnumerateArray().Select(ToOption).OfType<UserQuestionOption>().ToList()
            : [];
        if (options.Count == 0)
        {
            return null;
        }

        // The server's header is a non-nullable string, so an omitted header arrives as "": blank means absent.
        var header = ReadString(item, "header");
        return new UserQuestion
        {
            Header = string.IsNullOrWhiteSpace(header) ? null : header,
            Question = text,
            MultiSelect = item.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True,
            Options = options
        };
    }

    private static UserQuestionOption? ToOption(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && ReadString(item, "label") is { } label
            ? new UserQuestionOption
            {
                Label = label,
                Description = ReadString(item, "description"),
                Recommended = item.TryGetProperty("recommended", out var recommended) && recommended.ValueKind == JsonValueKind.True
            }
            : null;

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
