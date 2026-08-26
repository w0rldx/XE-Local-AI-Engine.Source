namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using System.Globalization;
using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     One artifact's bytes as JSON rather than a stream: a binary response would leave the generated SDK and need
///     hand-wiring on the client's HTTP layer for the one route that does not go through it.
/// </summary>
public sealed class GetWorkSessionArtifactContentEndpoint(IWorkSessionService service, IOptions<WorkSessionOptions> options)
    : Endpoint<WorkSessionArtifactRequest, WorkSessionArtifactContentResponse>
{
    private readonly WorkSessionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.ArtifactContent);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(WorkSessionArtifactRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The ceiling is checked against the RECORDED size before the blob is opened, so an over-ceiling artifact
        // is never read into memory to be refused afterwards. It can only exist if the operator lowered the cap
        // after the artifact was saved — the save path enforces the same number.
        var artifact = await _service.GetArtifactAsync(req.SessionId, req.ArtifactId, ct).ConfigureAwait(false);

        if (artifact.SizeBytes > _options.MaxArtifactBytes)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                          title: "Artifact too large",
                          detail: string.Create(CultureInfo.InvariantCulture,
                              $"The artifact is {artifact.SizeBytes} bytes, over this node's {_options.MaxArtifactBytes}-byte limit for reading one back.")))
                      .ConfigureAwait(false);
            return;
        }

        var content = await _service.ReadArtifactContentAsync(req.SessionId, req.ArtifactId, ct).ConfigureAwait(false);
        await Send.OkAsync(new WorkSessionArtifactContentResponse(content.Artifact.ToResponse(), content.Content, content.IsBase64), ct)
                  .ConfigureAwait(false);
    }
}
