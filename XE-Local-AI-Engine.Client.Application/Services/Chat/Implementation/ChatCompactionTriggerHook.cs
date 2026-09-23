namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Builds the post-turn automatic-compaction hook the pump fires on a terminal, and composes it with the
///     adaptive-memory hook.
/// </summary>
/// <remarks>
///     The hook only enqueues: the worker reloads the conversation, projects the next turn's history and decides, so
///     nothing here blocks the pump. It carries the window the runner reported for THIS turn (after
///     <c>TurnPolicy.WithEffectiveContext</c>), so the threshold matches what the turn's budgeter sized against.
/// </remarks>
internal static class ChatCompactionTriggerHook
{
    public static Action<InvocationState, NodeChatPumpTerminalResult> Build(IConversationMaintenanceDispatcher dispatcher, Guid conversationId)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        return (state, _) =>
        {
            // Only a finished answer grows the replayed history; a turn that never reported its window has nothing to size against.
            if (state.Status != InvocationStatus.Completed || state.ContextCapacityTokens is not { } capacity)
            {
                return;
            }

            dispatcher.Dispatch(new ConversationMaintenanceJob
            {
                ConversationId = conversationId,
                Kind = ConversationMaintenanceKind.Compact,
                ModelName = state.ModelUsed,
                ContextCapacityTokens = capacity,
                ReservedOutputTokens = state.ReservedOutputTokens ?? 0
            });
        };
    }

    /// <summary>Runs every non-null hook in order; a throw in one is logged by type name and never stops the next or reaches the pump.</summary>
    public static Action<InvocationState, NodeChatPumpTerminalResult> Compose(ILogger logger, params Action<InvocationState, NodeChatPumpTerminalResult>?[] hooks)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var active = hooks.OfType<Action<InvocationState, NodeChatPumpTerminalResult>>().ToArray();

        return (state, terminal) =>
        {
            foreach (var hook in active)
            {
                try
                {
                    hook(state, terminal);
                }
                catch (Exception exception)
                {
                    logger.LogWarning("A post-turn hook failed ({ErrorClass}); the chat turn is unaffected.", exception.GetType().Name);
                }
            }
        };
    }
}
