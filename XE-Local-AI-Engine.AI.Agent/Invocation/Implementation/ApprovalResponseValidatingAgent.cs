namespace XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
///     Validates replayed tool-approval responses before the inner agent can execute them.
/// </summary>
/// <remarks>
///     The framework correlates approval responses by request id. This boundary snapshots each request as the inner
///     agent surfaces it, then binds replayed requests and responses to that trusted snapshot. Caller-provided replay
///     history is therefore transport, not authority. The decorator is scoped to one invocation agent, so the snapshots
///     survive its threadless approval-resume rounds without crossing invocation boundaries.
/// </remarks>
internal sealed class ApprovalResponseValidatingAgent(AIAgent innerAgent) : DelegatingAIAgent(innerAgent)
{
    private readonly Lock _approvalLock = new();
    private readonly Dictionary<string, ApprovalResponseState> _responseStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolCallSnapshot> _surfacedRequests = new(StringComparer.Ordinal);

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var validated = Validate(messages);
        var response = await base.RunCoreAsync(validated.Messages, session, options, cancellationToken).ConfigureAwait(false);
        CaptureSurfacedRequests(response.Messages.SelectMany(static message => message.Contents));
        MarkResolved(validated.ReservedRequestIds);
        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var validated = Validate(messages);
        var completed = false;
        try
        {
            await foreach (var update in base.RunCoreStreamingAsync(validated.Messages, session, options, cancellationToken).ConfigureAwait(false))
            {
                if (update.Contents is { Count: > 0 } contents)
                {
                    CaptureSurfacedRequests(contents);
                }

                yield return update;
            }

            completed = true;
        }
        finally
        {
            if (completed)
            {
                MarkResolved(validated.ReservedRequestIds);
            }
        }
    }

    private ValidatedRun Validate(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var materialized = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        lock (_approvalLock)
        {
            var functionResultCallIds = materialized
                                        .SelectMany(static message => message.Contents)
                                        .OfType<FunctionResultContent>()
                                        .Select(static result => result.CallId)
                                        .Where(static callId => !string.IsNullOrEmpty(callId))
                                        .ToHashSet(StringComparer.Ordinal);
            var requests = new Dictionary<string, ToolCallSnapshot>(StringComparer.Ordinal);
            var responses = new HashSet<string>(StringComparer.Ordinal);
            var reservations = new List<string>();

            foreach (var content in materialized.SelectMany(static message => message.Contents))
            {
                switch (content)
                {
                    case ToolApprovalRequestContent request:
                        var replayedRequest = ToolCallSnapshot.Capture(request.ToolCall);
                        if (!requests.TryAdd(request.RequestId, replayedRequest)
                            && !requests[request.RequestId].Matches(request.ToolCall))
                        {
                            throw new InvalidOperationException($"Approval replay contains conflicting requests for request id '{request.RequestId}'.");
                        }

                        if (!_surfacedRequests.TryGetValue(request.RequestId, out var surfacedRequest)
                            || !surfacedRequest.Matches(request.ToolCall))
                        {
                            throw new InvalidOperationException($"Approval replay request id '{request.RequestId}' does not match a request surfaced by this invocation.");
                        }

                        break;

                    case ToolApprovalResponseContent response:
                        if (!responses.Add(response.RequestId))
                        {
                            throw new InvalidOperationException($"Approval replay contains more than one response for request id '{response.RequestId}'.");
                        }

                        if (!requests.TryGetValue(response.RequestId, out var matchingReplayRequest))
                        {
                            throw new InvalidOperationException($"Approval response request id '{response.RequestId}' does not match a pending approval request.");
                        }

                        if (!_surfacedRequests.TryGetValue(response.RequestId, out var matchingSurfacedRequest)
                            || !matchingReplayRequest.Matches(response.ToolCall)
                            || !matchingSurfacedRequest.Matches(response.ToolCall))
                        {
                            throw new InvalidOperationException($"Approval response request id '{response.RequestId}' does not match its originally surfaced tool call.");
                        }

                        switch (_responseStates[response.RequestId])
                        {
                            case ApprovalResponseState.Pending:
                                reservations.Add(response.RequestId);
                                break;

                            case ApprovalResponseState.Reserved:
                                throw new InvalidOperationException($"Approval response request id '{response.RequestId}' was already reserved; its execution outcome is uncertain.");

                            case ApprovalResponseState.Resolved:
                                if (string.IsNullOrEmpty(matchingSurfacedRequest.CallId)
                                    || !functionResultCallIds.Contains(matchingSurfacedRequest.CallId))
                                {
                                    throw new InvalidOperationException($"Approval response request id '{response.RequestId}' was already consumed.");
                                }

                                break;

                            default:
                                throw new InvalidOperationException($"Approval response request id '{response.RequestId}' has an invalid lifecycle state.");
                        }

                        break;
                }
            }

            foreach (var requestId in reservations)
            {
                _responseStates[requestId] = ApprovalResponseState.Reserved;
            }

            return new ValidatedRun(materialized, reservations);
        }
    }

    private void CaptureSurfacedRequests(IEnumerable<AIContent> contents)
    {
        lock (_approvalLock)
        {
            foreach (var request in contents.OfType<ToolApprovalRequestContent>())
            {
                var snapshot = ToolCallSnapshot.Capture(request.ToolCall);
                if (!_surfacedRequests.TryAdd(request.RequestId, snapshot)
                    && !_surfacedRequests[request.RequestId].Matches(request.ToolCall))
                {
                    throw new InvalidOperationException($"The inner agent surfaced conflicting approval requests for request id '{request.RequestId}'.");
                }

                _responseStates.TryAdd(request.RequestId, ApprovalResponseState.Pending);
            }
        }
    }

    private void MarkResolved(IReadOnlyList<string> requestIds)
    {
        lock (_approvalLock)
        {
            foreach (var requestId in requestIds)
            {
                if (_responseStates.GetValueOrDefault(requestId) == ApprovalResponseState.Reserved)
                {
                    _responseStates[requestId] = ApprovalResponseState.Resolved;
                }
            }
        }
    }

    private enum ApprovalResponseState
    {
        Pending,
        Reserved,
        Resolved
    }

    private sealed record ValidatedRun(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> ReservedRequestIds);

    private sealed record ToolCallSnapshot(
        Type RuntimeType,
        string? CallId,
        string FunctionName,
        JsonElement? Arguments)
    {
        internal static ToolCallSnapshot Capture(ToolCallContent toolCall)
        {
            ArgumentNullException.ThrowIfNull(toolCall);
            if (toolCall is not FunctionCallContent functionCall)
            {
                throw new InvalidOperationException($"Approval validation does not support tool-call type '{toolCall.GetType().FullName}'.");
            }

            return new ToolCallSnapshot(toolCall.GetType(),
                toolCall.CallId,
                functionCall.Name,
                CaptureArguments(functionCall.Arguments));
        }

        internal bool Matches(ToolCallContent actual)
        {
            if (actual.GetType() != RuntimeType
                || !string.Equals(CallId, actual.CallId, StringComparison.Ordinal)
                || actual is not FunctionCallContent functionCall
                || !string.Equals(FunctionName, functionCall.Name, StringComparison.Ordinal))
            {
                return false;
            }

            return ArgumentsMatch(Arguments, functionCall.Arguments);
        }

        private static JsonElement? CaptureArguments(IDictionary<string, object?>? arguments)
        {
            if (arguments is null)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
                return document.RootElement.Clone();
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw new InvalidOperationException("Approval tool-call arguments could not be snapshotted safely.",
                    exception);
            }
        }

        private static bool ArgumentsMatch(JsonElement? expected, IDictionary<string, object?>? actual)
        {
            if (expected is null || actual is null)
            {
                return expected is null && actual is null;
            }

            try
            {
                using var actualDocument = JsonDocument.Parse(JsonSerializer.Serialize(actual));
                return JsonElementsMatch(expected.Value, actualDocument.RootElement);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return false;
            }
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
