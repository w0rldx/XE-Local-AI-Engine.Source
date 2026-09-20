namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>Creates a manually authored golden conversation case for one agent. Operator-gated.</summary>
/// <remarks>
///     The endpoint serializes the typed input turns + assertion to camelCase JSON strings (the runner parses the same
///     shape) and delegates to the service, which validates a non-blank Title, an existing owning agent, non-empty
///     InputTurns and at least one of {Assertion, Rubric}. A validation failure surfaces as 400.
/// </remarks>
public sealed class CreateGoldenConversationEndpoint : Endpoint<CreateGoldenConversationRequest, GoldenConversationResponse>
{
    private readonly IGoldenConversationService _goldenConversationService;

    public CreateGoldenConversationEndpoint(IGoldenConversationService goldenConversationService)
    {
        ArgumentNullException.ThrowIfNull(goldenConversationService);
        _goldenConversationService = goldenConversationService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.GoldenConversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateGoldenConversationRequest req, CancellationToken ct)
    {
        var record = await _goldenConversationService.CreateAsync(req.ToInput(), ct);
        await Send.OkAsync(record.ToResponse(), ct);
    }
}
