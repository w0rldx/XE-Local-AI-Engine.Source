namespace XE_Local_AI_Engine.Testing.FakeOllama;

/// <summary>
///     Request DTO for fake ollama operations.
/// </summary>
public sealed record FakeOllamaRequest(
    string Method,
    string Path,
    string? ModelName,
    int MessageCount,
    string? PromptHash,
    DateTimeOffset CapturedAtUtc);
