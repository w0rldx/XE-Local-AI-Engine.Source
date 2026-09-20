namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Memory;

/// <summary>
///     Builds the post-run adaptive-memory hook the pump fires on a Completed or Failed terminal.
/// </summary>
/// <remarks>
///     The send and regenerate paths assemble the same metadata-only telemetry plus the content-bearing run input and
///     hand both to the background dispatcher; only the set of user turns they mine differs, so that arrives as a
///     deferred delegate the hook evaluates at invoke time. The dispatcher owns scope, token and error isolation, so
///     the returned delegate never blocks or throws into the pump.
/// </remarks>
internal static class ChatMemoryExtractionHook
{
    public static Action<InvocationState, NodeChatPumpTerminalResult> Build(IMemoryExtractionDispatcher dispatcher,
        ResolvedAgentRuntime resolved,
        Guid conversationId,
        bool memoryExcluded,
        RuntimePackage package,
        string? requestedModel,
        Func<IReadOnlyList<MemoryExtractionTurn>> collectUserTurns)
    {
        return (state, terminal) =>
        {
            // Only Completed/Failed terminals carry a real run to learn from; a Cancelled/Interrupted terminal is not a
            // finished answer, so skip it (no exec-log either — nothing meaningful ran).
            var failed = state.Status == InvocationStatus.Failed;
            if (state.Status != InvocationStatus.Completed && !failed)
            {
                return;
            }

            var modelName = state.ModelUsed ?? requestedModel ?? package.ModelProfile ?? string.Empty;
            var telemetry = new MemoryExtractionDispatchContext
            {
                AgentDefinitionId = resolved.AgentDefinitionId,
                ConversationId = conversationId,
                MessageId = terminal.Persisted.MessageId,
                ModelName = modelName,
                ConfigHash = package.ConfigHash,
                LatencyMs = state.GenerationDurationMs ?? 0,
                Success = !failed,
                PromptTokens = state.InputTokens,
                CompletionTokens = state.OutputTokens,
                // Exception TYPE NAME only when present — never the sanitized message text. FailureCategory is the only
                // type-shaped signal at this seam; the sanitized state.Error string is NOT logged.
                ErrorClass = failed ? state.FailureCategory?.ToString() : null
            };

            var run = new MemoryExtractionRunInput
            {
                AgentDefinitionId = resolved.AgentDefinitionId,
                ConversationId = conversationId,
                AssistantMessageId = terminal.Persisted.MessageId,
                UserTurns = collectUserTurns(),
                AssistantResponse = state.StreamedContent,
                Failed = failed,
                Error = state.Error,
                MemoryExcluded = memoryExcluded
            };

            dispatcher.Dispatch(telemetry, run);
        };
    }
}
