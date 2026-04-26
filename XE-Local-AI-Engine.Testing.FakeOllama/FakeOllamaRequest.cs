namespace XE_Local_AI_Engine.Testing.FakeOllama;

public sealed record FakeOllamaRequest(
    string Method,
    string Path,
    string? ModelName,
    int MessageCount,
    string? PromptHash,
    DateTimeOffset CapturedAtUtc);
