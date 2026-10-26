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
    ModelNotAssigned,

    /// <summary>A 500 from a gateway with no operation for the request path (a wrong base URL), which the node must not retry.</summary>
    NoRoute,

    /// <summary>A 404 for a model the gateway offers on the other API surface only.</summary>
    ApiNotSupported,

    /// <summary>A 401 from the gateway's backend RBAC refusing the model or operation, not the caller's credential.</summary>
    PermissionDenied,

    /// <summary>A 401 for an expired or refused bearer token.</summary>
    InvalidToken
}
