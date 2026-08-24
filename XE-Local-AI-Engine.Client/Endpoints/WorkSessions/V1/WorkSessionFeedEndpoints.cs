namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using System.Globalization;
using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

// The five feeds, artifact content, and the user follow-up. Every feed takes ?sinceSeq= so a hub notification costs
// one incremental read rather than a full refetch of the pane it named.

public sealed class ListWorkSessionTasksEndpoint(IWorkSessionService service) : Endpoint<WorkSessionFeedRequest, ListWorkSessionTasksResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Tasks);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            var tasks = await _service.ListTasksAsync(req.SessionId, req.SinceSeq, ct).ConfigureAwait(false);
            await Send.OkAsync(tasks.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListWorkSessionFindingsEndpoint(IWorkSessionService service) : Endpoint<WorkSessionFeedRequest, ListWorkSessionFindingsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Findings);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            var findings = await _service.ListFindingsAsync(req.SessionId, req.SinceSeq, ct).ConfigureAwait(false);
            await Send.OkAsync(findings.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListWorkSessionArtifactsEndpoint(IWorkSessionService service) : Endpoint<WorkSessionFeedRequest, ListWorkSessionArtifactsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Artifacts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            var artifacts = await _service.ListArtifactsAsync(req.SessionId, req.SinceSeq, ct).ConfigureAwait(false);
            await Send.OkAsync(artifacts.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListWorkSessionCheckpointsEndpoint(IWorkSessionService service)
    : Endpoint<WorkSessionFeedRequest, ListWorkSessionCheckpointsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Checkpoints);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            var checkpoints = await _service.ListCheckpointsAsync(req.SessionId, req.SinceSeq, ct).ConfigureAwait(false);
            await Send.OkAsync(checkpoints.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListWorkSessionEventsEndpoint(IWorkSessionService service) : Endpoint<WorkSessionEventFeedRequest, ListWorkSessionEventsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Events);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionEventFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            var events = await _service.ListEventsAsync(req.SessionId, req.SinceSeq, req.Limit, ct).ConfigureAwait(false);
            await Send.OkAsync(events.ToResponse(req.Limit), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

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

        try
        {
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
        catch (KeyNotFoundException)
        {
            // Unknown session, unknown artifact, and one whose bytes no longer verify all land here: no operator action
            // differs between them, and the artifact list already carries isValid for the pane that wants to say so.
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     A user follow-up into the session's owned conversation. 202: the row is persisted here, but the step that reads
///     it is the supervisor's — and on a Draft session there is no step until the operator starts it.
/// </summary>
public sealed class PostWorkSessionMessageEndpoint(IWorkSessionService service)
    : Endpoint<PostWorkSessionMessageRequest, PostWorkSessionMessageResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.WorkSessions.Messages);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<PostWorkSessionMessageResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(PostWorkSessionMessageRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            var messageId = await _service.PostFollowUpAsync(req.SessionId, req.Text, ct).ConfigureAwait(false);
            var session = await _service.GetAsync(req.SessionId, ct).ConfigureAwait(false);
            await Send.ResultAsync(Results.Accepted(value: new PostWorkSessionMessageResponse(messageId, session.ConversationId)))
                      .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}
