namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Renders a turn's replayed <see cref="ConversationToolExchange" /> list as the message pair a provider expects for
///     a tool round: one assistant message carrying ONLY the <see cref="FunctionCallContent" />, then one
///     <see cref="ChatRole.Tool" /> message carrying its <see cref="FunctionResultContent" />.
///     <para>
///         Shared rather than written twice on purpose. <c>InvocationRunner.BuildChatMessages</c> is what the model
///         actually receives, and <c>WorkSessionStepContextBound.Project</c> is the estimate the fold decision rests on;
///         a second rendering here would make the bound measure something the turn does not send, which is precisely the
///         failure the bound exists to prevent.
///     </para>
/// </summary>
internal static class ConversationToolExchangeMessages
{
    /// <summary>
    ///     Appends the call/result pair for every exchange, in list order. The assistant message deliberately carries no
    ///     text part: Microsoft.Extensions.AI's OpenAI client takes its tool-calls-only branch only when the message has
    ///     no content part, and an empty text part alongside <c>tool_calls</c> is what some chat templates reject.
    /// </summary>
    public static void Append(List<ChatMessage> messages, IReadOnlyList<ConversationToolExchange> exchanges)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(exchanges);

        foreach (var exchange in exchanges)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, [BuildCall(exchange)]));

            // The persisted result is passed UNWRAPPED: a string result is emitted verbatim by the provider adapters,
            // where a re-serialized object would reach the model quoted and escaped. An error result replays as its
            // text — the model acted on that text when it was live.
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(exchange.CallId, exchange.Result)]));
        }
    }

    private static FunctionCallContent BuildCall(ConversationToolExchange exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange.ArgumentsJson))
        {
            // CreateFromParsedArguments rejects a null encoding, and a call whose arguments were never recorded is a
            // call with no arguments rather than a failure.
            return new FunctionCallContent(exchange.CallId, exchange.Name);
        }

        return FunctionCallContent.CreateFromParsedArguments(exchange.ArgumentsJson, exchange.CallId, exchange.Name, TryParseArguments);
    }

    /// <summary>
    ///     Parses the recorded argument JSON, yielding null arguments rather than throwing: the parser runs inside
    ///     <see cref="FunctionCallContent.CreateFromParsedArguments{TEncoding}" />, which would otherwise stamp the
    ///     content with a parse <c>Exception</c> and turn a historical record into a live fault.
    /// </summary>
    private static IDictionary<string, object?>? TryParseArguments(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<IDictionary<string, object?>>(argumentsJson, AIJsonUtilities.DefaultOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
