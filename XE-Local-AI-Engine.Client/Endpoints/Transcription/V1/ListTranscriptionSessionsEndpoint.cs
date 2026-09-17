namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     The operator's transcription sessions, paged server-side so the page can reach rows older than the first one.
///     Ordered newest first by the store and never re-sorted here. Operator-gated.
/// </summary>
public sealed class ListTranscriptionSessionsEndpoint(ITranscriptionService sessions)
    : Endpoint<ListTranscriptionSessionsRequest, ListTranscriptionSessionsResponse>
{
    /// <summary>The page size a caller that names none gets.</summary>
    private const int DefaultLimit = 50;

    private readonly ITranscriptionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.Sessions);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ListTranscriptionSessionsResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(ListTranscriptionSessionsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var page = await _sessions.ListSessionsAsync(Math.Clamp(req.Limit ?? DefaultLimit, min: 1, ListTranscriptionSessionsRequestValidator.MaxLimit),
                                      Math.Max(req.Offset ?? 0, val2: 0),
                                      ct);

        await Send.OkAsync(new ListTranscriptionSessionsResponse
            {
                Items = [.. page.Items.Select(static session => session.ToResponse())],
                TotalCount = page.TotalCount
            },
            ct);
    }
}
