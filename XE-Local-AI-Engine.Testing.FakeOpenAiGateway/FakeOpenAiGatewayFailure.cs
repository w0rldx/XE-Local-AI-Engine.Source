namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>A fault the fake gateway answers the next API request with, after the bearer and header checks.</summary>
public enum FakeOpenAiGatewayFailure
{
    Unauthorized,
    Forbidden,
    UnknownModel,
    RateLimited,
    Http500,
    Hang,
    PartialStream,
    MalformedJson
}
