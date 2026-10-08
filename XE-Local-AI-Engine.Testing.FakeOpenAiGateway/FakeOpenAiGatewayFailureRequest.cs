namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>Body of <c>POST /test/failures</c>: a <see cref="FakeOpenAiGatewayFailure" /> name.</summary>
public sealed record FakeOpenAiGatewayFailureRequest
{
    public required string Failure { get; init; }
}
