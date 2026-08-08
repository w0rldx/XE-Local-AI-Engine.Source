namespace XE_Local_AI_Engine.Client.Services.Chat;

using OllamaSharp.Models;

public sealed record OllamaModelDetails(
    ShowModelResponse Response,
    int? MaxContextTokens,
    IReadOnlyList<string> Capabilities);
