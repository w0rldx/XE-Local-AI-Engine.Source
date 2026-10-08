namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>One API request as the fake gateway received it, recorded before any check rejected it.</summary>
public sealed record FakeOpenAiGatewayRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public string? Model { get; init; }

    public bool Stream { get; init; }

    public int MessageCount { get; init; }

    public int ToolCount { get; init; }

    /// <summary>Every request header, name to value, compared case-insensitively; values are verbatim.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
}
