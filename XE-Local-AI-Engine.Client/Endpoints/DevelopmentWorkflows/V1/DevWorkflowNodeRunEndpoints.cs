namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using System.Globalization;
using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Common;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     One node run in full. The agent view is reached from here through <c>workSessionId</c> and the EXISTING
///     work-session routes rather than through observability endpoints of this surface's own — two ways to read one
///     session's events would drift.
/// </summary>
public sealed class GetDevWorkflowNodeRunEndpoint(DevWorkflowRunComposer composer) : Endpoint<DevWorkflowNodeRunRequest, DevWorkflowNodeRunDetailResponse>
{
    private readonly DevWorkflowRunComposer _composer = composer ?? throw new ArgumentNullException(nameof(composer));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.NodeRunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowNodeRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await Send.OkAsync(await _composer.ComposeNodeAsync(req.RunId, req.NodeRunId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     The run's artifacts, every version of every lineage. The version history IS the knowledge layer, so hiding
///     superseded rows behind a flag would cost an endpoint to get them back; the client groups by
///     <c>lineageId</c> and reads <c>isLatest</c>, which is computed here rather than re-derived there.
/// </summary>
public sealed class ListDevWorkflowArtifactsEndpoint(IDevWorkflowStore store) : Endpoint<DevWorkflowArtifactFeedRequest, ListDevWorkflowArtifactsResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RunArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowArtifactFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The run is read first so an unknown one answers 404 rather than an empty page.
        _ = await _store.GetRunAsync(req.RunId, ct).ConfigureAwait(false);

        var artifacts = await _store.ListArtifactsAsync(req.RunId, req.SinceSeq, ct).ConfigureAwait(false);
        var items = artifacts.Select(DevWorkflowContractMapper.ToResponse).ToList();
        await Send.OkAsync(new ListDevWorkflowArtifactsResponse(items, DevWorkflowContractMapper.HighestSequence(items.Select(static item => item.Sequence))), ct)
                  .ConfigureAwait(false);
    }
}

/// <summary>
///     One artifact's bytes as JSON rather than a stream: a binary response would leave the generated SDK and need
///     hand-wiring on the client's HTTP layer for the one route that does not go through it.
/// </summary>
public sealed class GetDevWorkflowArtifactContentEndpoint(IDevWorkflowStore store, IDevWorkflowArtifactBlobStore blobs, IOptions<DevWorkflowOptions> options)
    : Endpoint<DevWorkflowArtifactRequest, DevWorkflowArtifactContentResponse>
{
    private readonly IDevWorkflowArtifactBlobStore _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
    private readonly DevWorkflowOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RunArtifactContent);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(DevWorkflowArtifactRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var artifact = await _store.GetArtifactAsync(req.ArtifactId, ct).ConfigureAwait(false);
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
                              $"The artifact is {artifact.SizeBytes} bytes, over this node's {_options.MaxArtifactBytes}-byte limit for reading one back.")))
                      .ConfigureAwait(false);
            return;
        }

        var read = await _blobs.ReadAsync(req.RunId, req.ArtifactId, artifact.ContentSha256, artifact.SizeBytes, ct).ConfigureAwait(false);
        if (read.Status != DevWorkflowArtifactReadStatus.Found)
        {
            // Bytes the node cannot vouch for are not bytes it hands over. The row stays; the read reads as "gone".
            throw new DevWorkflowNotFoundException($"Development workflow artifact '{req.ArtifactId}' could not be read ({read.Status}).");
        }

        var isBase64 = !ArtifactMediaTypes.IsText(artifact.MediaType);
        var content = isBase64 ? Convert.ToBase64String(read.Content.Span) : System.Text.Encoding.UTF8.GetString(read.Content.Span);
        await Send.OkAsync(new DevWorkflowArtifactContentResponse(artifact.ToResponse(), content, isBase64), ct).ConfigureAwait(false);
    }
}
