namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>The pre-run notices a send and a regenerate share, in wire order.</summary>
internal static class ChatPreRunNotices
{
    public static async Task ReportCommonAsync(IWorkerEventDispatcher dispatcher,
        ChatTurnResolution resolution,
        ChatToolOffer toolOffer,
        ILocalToolOfferProvider offerProvider,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        // A bound agent that no longer exists falls back to the default assistant; say so instead of answering silently.
        if (resolution.AgentUnavailableNotice(requestId) is { } agentUnavailableNotice)
        {
            await dispatcher.ReportTurnNoticeAsync(agentUnavailableNotice);
        }

        // An Orchestrator agent whose orchestration did not compile runs as a lone single agent, so emit ONE notice
        // naming the typed reason. NotOrchestrated (a Single-kind or unbound agent) has none, so the common path is silent.
        if (resolution.OrchestrationOutcome.DegradationNotice is { } orchestrationDegradedMessage)
        {
            await dispatcher.ReportTurnNoticeAsync(new TurnNoticePayload
            {
                InvocationId = requestId,
                Kind = TurnNoticeKind.OrchestrationDegraded,
                Message = orchestrationDegradedMessage,
                Detail = resolution.OrchestrationOutcome.Reason.ToString()
            });
        }

        if (resolution.PlaybookWithheldNotice(requestId) is { } playbookWithheldNotice)
        {
            await dispatcher.ReportTurnNoticeAsync(playbookWithheldNotice);
        }

        if (toolOffer.WithheldForCapability)
        {
            await dispatcher.ReportTurnNoticeAsync(resolution.ToolsWithheldNotice(requestId));
        }

        if (toolOffer.TrimmedByAllowList)
        {
            await dispatcher.ReportTurnNoticeAsync(resolution.ToolsNotAllowListedNotice(requestId));
        }

        if (resolution.ComputeWithheldNotice(requestId) is { } computeWithheldNotice)
        {
            await dispatcher.ReportTurnNoticeAsync(computeWithheldNotice);
        }

        // A compiled orchestration's participants carry their own tools whatever the client asked.
        if ((toolOffer.OfferTools || resolution.Orchestration is not null)
            && await resolution.CloudToolsWithheldNoticeAsync(requestId, offerProvider, cancellationToken) is { } cloudToolsWithheldNotice)
        {
            await dispatcher.ReportTurnNoticeAsync(cloudToolsWithheldNotice);
        }
    }
}
