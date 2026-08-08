namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows.Implementation;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

/// <summary>
///     Builds the non-agent MAF executors for a Preview workflow: Start (seed), transform (input isolation),
///     Debug-tap (side-event + forward-unchanged), and End (terminal output). Encapsulates the MAF
///     discipline: agent executors run the ChatProtocol — they ACCUMULATE inbound ChatMessages and only run+forward
///     the WHOLE accumulated conversation on a TurnToken, so:
///     - Start/transform send a fresh single-message user list + a TurnToken (declare both sentMessageTypes).
///     - the transform extracts ONLY the upstream agent's latest assistant text (never concat the whole list).
/// </summary>
internal static class PreviewExecutors
{
    private static readonly Type[] AgentDriveMessageTypes = [typeof(List<ChatMessage>), typeof(TurnToken)];

    /// <summary>
    ///     Start node: receives the seed text as the workflow input and drives the first agent by sending a single
    ///     user message list + a TurnToken (agents only run on a TurnToken — OrchestrationAgentFactory.cs:91).
    /// </summary>
    public static FunctionExecutor<string> BuildStart(string id, string seedText, string targetId)
    {
        return new FunctionExecutor<string>(id,
            async (_, context, cancellationToken) =>
            {
                List<ChatMessage> seed = [new(ChatRole.User, seedText)];
                await context.SendMessageAsync(seed, targetId, cancellationToken).ConfigureAwait(false);
                await context.SendMessageAsync(new TurnToken(true), targetId, cancellationToken).ConfigureAwait(false);
            },
            sentMessageTypes: AgentDriveMessageTypes);
    }

    /// <summary>
    ///     Transform node (between two agents): isolates the next agent's input to ONLY the upstream agent's latest
    ///     assistant text. Extracts the last <see cref="ChatRole.Assistant" /> message from the
    ///     forwarded accumulated conversation, emits a FRESH single user message list, and sends a TurnToken so the
    ///     downstream agent actually runs. Never concatenates the whole forwarded list (which would re-include the
    ///     prior user turn and break per-agent input isolation).
    /// </summary>
    public static FunctionExecutor<List<ChatMessage>> BuildTransform(string id, string targetId)
    {
        return new FunctionExecutor<List<ChatMessage>>(id,
            async (messages, context, cancellationToken) =>
            {
                var text = ExtractLatestAssistantText(messages);
                List<ChatMessage> next = [new(ChatRole.User, text)];
                await context.SendMessageAsync(next, targetId, cancellationToken).ConfigureAwait(false);
                await context.SendMessageAsync(new TurnToken(true), targetId, cancellationToken).ConfigureAwait(false);
            },
            sentMessageTypes: AgentDriveMessageTypes);
    }

    /// <summary>
    ///     Debug-print node: a tap. Emits the upstream payload as a <see cref="PreviewDebugEvent" /> side event
    ///     (AddEventAsync — NOT routed) and returns the payload UNCHANGED (auto-forwarded) so the edge does not fork.
    ///     The payload is the upstream agent's accumulated conversation; we surface its latest
    ///     assistant text for display but forward the full list unchanged to preserve downstream agent semantics.
    /// </summary>
    public static FunctionExecutor<List<ChatMessage>, List<ChatMessage>> BuildDebugTap(string nodeId)
    {
        return new FunctionExecutor<List<ChatMessage>, List<ChatMessage>>(nodeId,
            async (messages, context, cancellationToken) =>
            {
                var display = ExtractLatestAssistantText(messages);
                await context.AddEventAsync(new PreviewDebugEvent(nodeId, display), cancellationToken).ConfigureAwait(false);
                return messages;
            });
    }

    /// <summary>
    ///     Pre-pause adapter: converts the upstream agent's accumulated conversation into the single <c>string</c> the
    ///     <c>RequestPort&lt;string,string&gt;</c> expects (the latest assistant text — the value the operator sees in
    ///     the pause display). Auto-forwarded as the port's request payload.
    /// </summary>
    public static FunctionExecutor<List<ChatMessage>, string> BuildPausePreAdapter(string id)
    {
        return new FunctionExecutor<List<ChatMessage>, string>(id,
            (messages, _, _) => new ValueTask<string>(ExtractLatestAssistantText(messages)));
    }

    /// <summary>
    ///     Post-pause adapter: after the port resumes (its response string carries the original upstream output — the
    ///     session echoes the request data back as the response, NOT a bare "CONTINUE"), re-seeds the chain as a fresh
    ///     single user message + a TurnToken targeted at the downstream executor (so a downstream Agent runs; End
    ///     ignores the TurnToken).
    /// </summary>
    public static FunctionExecutor<string> BuildPausePostAdapter(string id, string targetId)
    {
        return new FunctionExecutor<string>(id,
            async (resumed, context, cancellationToken) =>
            {
                List<ChatMessage> next = [new(ChatRole.User, resumed)];
                await context.SendMessageAsync(next, targetId, cancellationToken).ConfigureAwait(false);
                await context.SendMessageAsync(new TurnToken(true), targetId, cancellationToken).ConfigureAwait(false);
            },
            sentMessageTypes: AgentDriveMessageTypes);
    }

    /// <summary>
    ///     End node: terminal sink. Yields the final assistant text as the workflow output (marked terminal via
    ///     <c>WithOutputFrom</c>). Receives the upstream agent's accumulated conversation; surfaces its latest
    ///     assistant text as the run result.
    /// </summary>
    public static FunctionExecutor<List<ChatMessage>> BuildEnd(string id)
    {
        return new FunctionExecutor<List<ChatMessage>>(id,
            async (messages, context, cancellationToken) =>
            {
                var output = ExtractLatestAssistantText(messages);
                await context.YieldOutputAsync(output, cancellationToken).ConfigureAwait(false);
            },
            outputTypes: [typeof(string)]);
    }

    /// <summary>
    ///     Extracts the latest <see cref="ChatRole.Assistant" /> message text from a forwarded conversation. Agents
    ///     forward their WHOLE accumulated conversation (user turn(s) + assistant response); we want only the newest
    ///     assistant content. Falls back to the last message's text if no assistant turn is present.
    /// </summary>
    private static string ExtractLatestAssistantText(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.Assistant)
            {
                return messages[i].Text ?? string.Empty;
            }
        }

        return messages.Count > 0 ? messages[^1].Text ?? string.Empty : string.Empty;
    }
}
