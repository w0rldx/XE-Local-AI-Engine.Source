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

    /// <summary>The capped text read, renamed onto the wire.</summary>
    /// <remarks>
    ///     Deliberately NOT sanitised: this is the one AgentHome contract whose payload is the run's own bytes, and
    ///     rewriting them would hand the operator a document that is not what the run wrote. What keeps it honest is
    ///     where it is rendered — a read-only Monaco surface with its default unicode highlighting on, which flags
    ///     invisible, ambiguous and control characters in place.
    /// </remarks>
    public static AgentHomeRunTextResponse ToResponse(this AgentHomeRunText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new AgentHomeRunTextResponse { Text = text.Text, Truncated = text.Truncated };
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
