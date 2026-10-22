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
    MalformedJson,

    /// <summary>A 403 in a policy gateway's own shape (<c>allowed</c>, <c>reason</c>, echoed ids) refusing the model, not the credential.</summary>
    ModelNotAssigned
}
