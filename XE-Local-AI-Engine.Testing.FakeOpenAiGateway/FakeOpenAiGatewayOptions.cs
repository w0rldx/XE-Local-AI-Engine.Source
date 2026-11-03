namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>What a <see cref="FakeOpenAiGatewayServer" /> demands of a caller and what it answers with.</summary>
public sealed record FakeOpenAiGatewayOptions
{
    /// <summary>The token <c>Authorization: Bearer</c> must carry, or null to skip the bearer check.</summary>
    public string? RequiredBearerToken { get; init; }

    /// <summary>Headers every API request must carry with exactly this value; a miss answers 403.</summary>
    public IReadOnlyDictionary<string, string> RequiredHeaders { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The initial model list; a chat or embedding request naming any other model answers 404 unless <see cref="AllowUnlistedModels" />.</summary>
    public IReadOnlyList<FakeOpenAiGatewayModel> Models { get; init; } =
    [
        new FakeOpenAiGatewayModel
        {
            Id = "gateway-demo-model",
            ContextLength = 32768
        }
    ];

    /// <summary>Serve a request for a model the list does not name, as a node-local runtime stand-in must for test-chosen model ids.</summary>
    public bool AllowUnlistedModels { get; init; }

    /// <summary>The assistant reply when no script is queued.</summary>
    public string DefaultCompletionText { get; init; } = "Hello from the fake gateway.";

    /// <summary>When set, the unscripted chat reply is this prefix, a space and the last user message, instead of <see cref="DefaultCompletionText" />.</summary>
    public string? EchoPrefix { get; init; }

    /// <summary>The vector length <c>POST embeddings</c> answers when the request names none.</summary>
    public int EmbeddingDimensions { get; init; } = 384;

    /// <summary>The <c>X-Test-Sink-Token</c> the <c>/test/*</c> control endpoints demand, or null for none.</summary>
    public string? ControlEndpointToken { get; init; }

    /// <summary>The API prefix, as an OpenAI-compatible base URL carries it.</summary>
    public string BasePath { get; init; } = "/v1";

    /// <summary>The loopback port to bind; 0 picks a free one.</summary>
    public int Port { get; init; }
}
