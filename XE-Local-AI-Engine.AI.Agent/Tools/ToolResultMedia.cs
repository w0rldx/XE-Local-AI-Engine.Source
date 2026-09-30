namespace XE_Local_AI_Engine.AI.Agent.Tools;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

/// <summary>
///     Carries a tool result's binary blocks (images, audio) beside the TEXT the model receives, keyed on that exact
///     result string instance.
/// </summary>
/// <remarks>
///     The provider boundary sends a non-string tool result as serialized JSON, so an image block would reach the model
///     as base64 text. The model therefore gets a string with a placeholder, and the chat stream, which sees the same
///     <c>FunctionResultContent.Result</c> reference, looks the blocks up here to persist and render them. Weak keys: an
///     entry lives exactly as long as its result string.
/// </remarks>
internal static class ToolResultMedia
{
    private static readonly ConditionalWeakTable<string, IReadOnlyList<DataContent>> MediaByResult = new();

    public static void Attach(string result, IReadOnlyList<DataContent> media)
    {
        if (media.Count > 0)
        {
            MediaByResult.AddOrUpdate(result, media);
        }
    }

    public static IReadOnlyList<DataContent> Get(object? result) =>
        result is string text && MediaByResult.TryGetValue(text, out var media) ? media : [];
}
