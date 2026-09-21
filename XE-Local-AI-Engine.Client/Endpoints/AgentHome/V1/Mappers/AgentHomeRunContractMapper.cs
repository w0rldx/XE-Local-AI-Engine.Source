namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     Projects <see cref="IAgentHomeRunListService" />'s page onto the wire. A rename and nothing else: the service
///     already produces only node-minted ids and closed tokens, so there is nothing here for a mapper to strip.
/// </summary>
internal static class AgentHomeRunContractMapper
{
    public static ListAgentHomeRunsResponse ToResponse(this AgentHomeRunPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new ListAgentHomeRunsResponse
        {
            Items = [.. page.Items.Select(ToDto)],
            TotalCount = page.TotalCount
        };
    }

    private static AgentHomeRunDto ToDto(AgentHomeRunSummary summary)
    {
        return new AgentHomeRunDto
        {
            RunId = summary.RunId,
            StartedAtUtc = summary.StartedAtUtc,
            Outcome = summary.Outcome,
            PatchExported = summary.PatchExported,
            ChangedFileCount = summary.ChangedFileCount,
            ApplyState = summary.ApplyState,
            ConversationId = summary.ConversationId,
            SizeBytes = summary.SizeBytes
        };
    }
}
