namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     Tests a connection — stored or not yet saved — with one server-side <c>GET {base}/models</c>, and returns the
///     model ids for pick-to-add.
/// </summary>
/// <remarks>
///     <para>
///         The probe runs on the node because the browser cannot reach an arbitrary operator endpoint through CORS,
///         and because the stored API key exists only here. An inline draft under a stored connection id falls back to
///         that connection's key, so "Test connection" works on an existing connection without re-typing the secret —
///         and the key is never returned, only used.
///     </para>
///     <para>
///         A reachable endpoint that serves no model listing is a 200 with <c>reachable: true</c>, an explanatory
///         <c>error</c> and an empty list, not a failure: only <c>POST /v1/chat/completions</c> is universal across
///         OpenAI-compatible servers, so refusing such a connection would refuse working ones.
///     </para>
/// </remarks>
public sealed class ProbeExternalProviderEndpoint(IExternalProviderProbeService probeService)
    : Endpoint<ExternalProviderProbeRequest, ExternalProviderProbeResponse>
{
    private readonly IExternalProviderProbeService _probeService = probeService ?? throw new ArgumentNullException(nameof(probeService));

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalProviders.Probe);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ExternalProviderProbeResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ExternalProviderProbeRequest req, CancellationToken ct)
    {
        var result = await _probeService
                           .ProbeAsync(new ExternalProviderProbeQuery(req.ConnectionId, req.BaseUrl, req.ApiKey), ct)
                           .ConfigureAwait(false);

        switch (result.Outcome)
        {
            // Nothing was sent: the request named a connection that is not stored. A 404 rather than a
            // reachable:false, which would read as "your server is down".
            case ExternalProviderProbeOutcome.UnknownConnection:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;

            // Also nothing sent, but this one the operator can fix by editing the field they just typed.
            case ExternalProviderProbeOutcome.InvalidBaseUrl:
                AddError(result.Error ?? "The endpoint is not a valid OpenAI-compatible base URL.");
                await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
                return;
            default:
                await Send.OkAsync(result.ToResponse(), ct).ConfigureAwait(false);
                return;
        }
    }
}
