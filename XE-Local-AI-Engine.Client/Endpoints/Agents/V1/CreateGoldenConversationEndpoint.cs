namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Creates a manually authored golden conversation case for one agent. The endpoint serializes the
///     typed input turns + assertion to camelCase JSON strings (the runner parses the same shape) and delegates to the
///     service, which validates a non-blank Title, an existing owning agent, non-empty InputTurns and at least one of
///     {Assertion, Rubric}. A validation failure surfaces as 400. Operator-gated.
/// </summary>
public sealed class CreateGoldenConversationEndpoint(IGoldenConversationService goldenConversationService)
    : Endpoint<CreateGoldenConversationRequest, GoldenConversationResponse>
{
    private readonly IGoldenConversationService _goldenConversationService = goldenConversationService ?? throw new ArgumentNullException(nameof(goldenConversationService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.GoldenConversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateGoldenConversationRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _goldenConversationService.CreateAsync(req.ToInput(), ct).ConfigureAwait(false);
            await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (PlaybookActionValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
