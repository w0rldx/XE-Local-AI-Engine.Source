namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using System.Globalization;
using System.Text;
using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     One artifact's bytes as JSON rather than a stream: a binary response would leave the generated SDK and need
///     hand-wiring on the client's HTTP layer for the one route that does not go through it.
/// </summary>
public sealed class GetDevWorkflowArtifactContentEndpoint : Endpoint<DevWorkflowArtifactRequest, DevWorkflowArtifactContentResponse>
{
    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly DevWorkflowOptions _options;
    private readonly DevWorkflowRunQueryService _runQueries;

    public GetDevWorkflowArtifactContentEndpoint(DevWorkflowRunQueryService runQueries, IDevWorkflowArtifactBlobStore blobs, IOptions<DevWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        _blobs = blobs;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(runQueries);
        _runQueries = runQueries;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RunArtifactContent);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(DevWorkflowArtifactRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var artifact = await _runQueries.GetArtifactAsync(req.ArtifactId, ct);
        if (artifact.RunId != req.RunId || !artifact.IsValid)
        {
            // An artifact of another run — or one the node already marked invalid — reads as absent, so one run's
            // route can never hand over another's bytes.
            throw new DevWorkflowNotFoundException($"Development workflow artifact '{req.ArtifactId}' was not found on run '{req.RunId}'.");
        }

        // The ceiling is checked against the RECORDED size before the blob is opened, so an over-ceiling artifact is
        // never read into memory to be refused afterwards.
        if (artifact.SizeBytes > _options.MaxArtifactBytes)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                          title: "Artifact too large",
                          detail: string.Create(CultureInfo.InvariantCulture,
                              $"The artifact is {artifact.SizeBytes} bytes, over this node's {_options.MaxArtifactBytes}-byte limit for reading one back.")));
            return;
        }

        var read = await _blobs.ReadAsync(req.RunId, req.ArtifactId, artifact.ContentSha256, artifact.SizeBytes, ct);
        if (read.Status != DevWorkflowArtifactReadStatus.Found)
        {
            // Bytes the node cannot vouch for are not bytes it hands over. The row stays; the read reads as "gone".
            throw new DevWorkflowNotFoundException($"Development workflow artifact '{req.ArtifactId}' could not be read ({read.Status}).");
        }

        var isBase64 = !ArtifactMediaTypes.IsText(artifact.MediaType);
        var content = isBase64 ? Convert.ToBase64String(read.Content.Span) : Encoding.UTF8.GetString(read.Content.Span);
        await Send.OkAsync(new DevWorkflowArtifactContentResponse { Artifact = artifact.ToResponse(), Content = content, IsBase64 = isBase64 }, ct);
    }
}
