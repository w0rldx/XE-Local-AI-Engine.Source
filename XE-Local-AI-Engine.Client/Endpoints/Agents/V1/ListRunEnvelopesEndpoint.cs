namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Read-only, versioned durable run-envelope lifecycle records (MED-007 / R4). Returns a page of metadata-only rows
///     (terminal status, usage/timing counters, correlation ids, trace id, schema version) newest-first, optionally
///     scoped to one conversation — there is NO message content in this store, so nothing to redact; <c>FailureCategory</c>
///     is a category enum name only by the store contract. Operator-gated.
/// </summary>
public sealed class ListRunEnvelopesEndpoint(IAgentExecutionLogStore executionLogStore)
    : Endpoint<ListRunEnvelopesRequest, ListRunEnvelopesResponse>
{
    // Default page size when the caller supplies none; clamped upper bound keeps a diagnostics fetch bounded.
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly IAgentExecutionLogStore _executionLogStore = executionLogStore ?? throw new ArgumentNullException(nameof(executionLogStore));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.RunEnvelopes);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListRunEnvelopesRequest req, CancellationToken ct)
    {
        var limit = req.Limit is { } requested && requested > 0 ? Math.Min(requested, MaxPageSize) : DefaultPageSize;
        var offset = req.Offset is { } requestedOffset && requestedOffset > 0 ? requestedOffset : 0;

        var records = await _executionLogStore.ListRunEnvelopesAsync(req.ConversationId, limit, offset, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListRunEnvelopesResponse
            {
                Items = [.. records.Select(static record => record.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
