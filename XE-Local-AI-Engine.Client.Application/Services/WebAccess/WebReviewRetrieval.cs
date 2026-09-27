namespace XE_Local_AI_Engine.Client.Services.WebAccess;

using System.Text.Json;

/// <summary>One web call's retrieval, held between the fetch and the user's review.</summary>
/// <remarks>
///     <see cref="ModelText" /> is what the model receives on accept; a <see langword="null" /> <see cref="Preview" />
///     means there is nothing to review (a refusal or a malformed call), so the text goes to the model without a card.
/// </remarks>
internal sealed class WebReviewRetrieval
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public required string ModelText { get; init; }

    public WebReviewPreview? Preview { get; init; }

    /// <summary>A result the model reads with no review, in the refusal shape the web services use.</summary>
    public static WebReviewRetrieval Refusal(string code, string message) =>
        new()
        {
            ModelText = RefusalJson(code, message)
        };

    public static string RefusalJson(string code, string message) =>
        JsonSerializer.Serialize(new
            {
                error = code,
                message
            },
            SerializerOptions);
}
