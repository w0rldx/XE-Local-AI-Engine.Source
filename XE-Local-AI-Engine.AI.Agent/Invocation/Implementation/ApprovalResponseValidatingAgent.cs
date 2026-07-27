namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
///     Validates replayed tool-approval responses before the inner agent can execute them.
/// </summary>
/// <remarks>
///     The framework correlates approval responses by request id. This boundary additionally binds each response to the
///     exact tool call that originally produced the request, and rejects unmatched or duplicate responses. It decorates
///     the agent rather than its chat client so validation occurs before MAF's own approval middleware transforms the
///     caller-provided replay history.
/// </remarks>
internal sealed class ApprovalResponseValidatingAgent(AIAgent innerAgent) : DelegatingAIAgent(innerAgent)
{
    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var validated = Validate(messages);
        return base.RunCoreAsync(validated, session, options, cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var validated = Validate(messages);
        await foreach (var update in base.RunCoreStreamingAsync(validated, session, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static IReadOnlyList<ChatMessage> Validate(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        var requests = new Dictionary<string, ToolApprovalRequestContent>(StringComparer.Ordinal);
        var responses = new HashSet<string>(StringComparer.Ordinal);

        foreach (var content in materialized.SelectMany(static message => message.Contents))
        {
            switch (content)
            {
                case ToolApprovalRequestContent request:
                    if (!requests.TryAdd(request.RequestId, request)
                        && !ToolCallsMatch(requests[request.RequestId].ToolCall, request.ToolCall))
                    {
                        throw new InvalidOperationException(
                            $"Approval replay contains conflicting requests for request id '{request.RequestId}'.");
                    }

                    break;

                case ToolApprovalResponseContent response:
                    if (!responses.Add(response.RequestId))
                    {
                        throw new InvalidOperationException(
                            $"Approval replay contains more than one response for request id '{response.RequestId}'.");
                    }

                    if (!requests.TryGetValue(response.RequestId, out var matchingRequest))
                    {
                        throw new InvalidOperationException(
                            $"Approval response request id '{response.RequestId}' does not match a pending approval request.");
                    }

                    if (!ToolCallsMatch(matchingRequest.ToolCall, response.ToolCall))
                    {
                        throw new InvalidOperationException(
                            $"Approval response request id '{response.RequestId}' does not match its original tool call.");
                    }

                    break;
            }
        }

        return materialized;
    }

    private static bool ToolCallsMatch(ToolCallContent expected, ToolCallContent actual)
    {
        if (expected.GetType() != actual.GetType()
            || !string.Equals(expected.CallId, actual.CallId, StringComparison.Ordinal))
        {
            return false;
        }

        return (expected, actual) switch
        {
            (FunctionCallContent expectedFunction, FunctionCallContent actualFunction) =>
                string.Equals(expectedFunction.Name, actualFunction.Name, StringComparison.Ordinal)
                && ArgumentsMatch(expectedFunction.Arguments, actualFunction.Arguments),
            _ => ReferenceEquals(expected, actual)
        };
    }

    private static bool ArgumentsMatch(IDictionary<string, object?>? expected, IDictionary<string, object?>? actual)
    {
        if (ReferenceEquals(expected, actual))
        {
            return true;
        }

        if (expected is null || actual is null)
        {
            return false;
        }

        try
        {
            using var expectedDocument = JsonDocument.Parse(JsonSerializer.Serialize(expected));
            using var actualDocument = JsonDocument.Parse(JsonSerializer.Serialize(actual));
            return JsonElementsMatch(expectedDocument.RootElement, actualDocument.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool JsonElementsMatch(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }

        return expected.ValueKind switch
        {
            JsonValueKind.Object => ObjectPropertiesMatch(expected, actual),
            JsonValueKind.Array => expected.GetArrayLength() == actual.GetArrayLength()
                                   && expected.EnumerateArray()
                                              .Zip(actual.EnumerateArray())
                                              .All(static pair => JsonElementsMatch(pair.First, pair.Second)),
            JsonValueKind.String => string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => string.Equals(expected.GetRawText(), actual.GetRawText(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => expected.GetBoolean() == actual.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => false
        };
    }

    private static bool ObjectPropertiesMatch(JsonElement expected, JsonElement actual)
    {
        var expectedProperties = expected.EnumerateObject().ToDictionary(static property => property.Name,
            static property => property.Value,
            StringComparer.Ordinal);
        var actualProperties = actual.EnumerateObject().ToDictionary(static property => property.Name,
            static property => property.Value,
            StringComparer.Ordinal);

        return expectedProperties.Count == actualProperties.Count
               && expectedProperties.All(pair => actualProperties.TryGetValue(pair.Key, out var value)
                                                 && JsonElementsMatch(pair.Value, value));
    }
}
