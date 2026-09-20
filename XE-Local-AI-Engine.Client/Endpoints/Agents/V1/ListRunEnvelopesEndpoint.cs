namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Read-only, versioned durable run-envelope lifecycle records: a page of metadata-only rows (terminal status,
///     usage/timing counters, correlation ids, trace id, schema version), newest-first. Operator-gated.
/// </summary>
/// <remarks>
///     Optionally scoped to one conversation. There is NO message content in this store, so nothing to redact, and
///     <c>FailureCategory</c> is a category enum name only by the store contract.
/// </remarks>
public sealed class ListRunEnvelopesEndpoint : Endpoint<ListRunEnvelopesRequest, ListRunEnvelopesResponse>
{
    // Default page size when the caller supplies none; clamped upper bound keeps a diagnostics fetch bounded.
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly AgentExecutionLogQueryService _executionLogs;

    public ListRunEnvelopesEndpoint(AgentExecutionLogQueryService executionLogs)
    {
        ArgumentNullException.ThrowIfNull(executionLogs);
        _executionLogs = executionLogs;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.RunEnvelopes);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListRunEnvelopesRequest req, CancellationToken ct)
    {
        var limit = req.Limit is { } requested && requested > 0 ? Math.Min(requested, MaxPageSize) : DefaultPageSize;
        var offset = req.Offset is { } requestedOffset && requestedOffset > 0 ? requestedOffset : 0;

        var records = await _executionLogs.ListRunEnvelopesAsync(req.ConversationId, limit, offset, ct);
        await Send.OkAsync(new ListRunEnvelopesResponse
            {
                Items = [.. records.Select(static record => record.ToResponse())]
            },
            ct);
    }
}
