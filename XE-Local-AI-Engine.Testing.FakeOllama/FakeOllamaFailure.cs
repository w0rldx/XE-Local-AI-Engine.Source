namespace XE_Local_AI_Engine.Testing.FakeOllama;

/// <summary>
///     Enumerates supported fake ollama failure values.
/// </summary>
public enum FakeOllamaFailure
{
    ModelUnavailable,
    Timeout,
    MalformedJson,
    EmptyResponse,
    PartialStream,
    Http500
}
