namespace XE_Local_AI_Engine.Client.Services.Chat;

using OllamaSharp.Models;

/// <summary>
///     Value object carrying ollama model details data.
/// </summary>
public sealed record OllamaModelDetails(
    ShowModelResponse Response,
    int? MaxContextTokens,
    IReadOnlyList<string> Capabilities);
