namespace XE_Local_AI_Engine.Testing.FakeOllama;

public enum FakeOllamaFailure
{
    ModelUnavailable,
    Timeout,
    MalformedJson,
    EmptyResponse,
    PartialStream,
    Http500
}
