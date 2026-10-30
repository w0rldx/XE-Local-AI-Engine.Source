namespace XE_Local_AI_Engine.Cli.Streaming;

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Cli.Auth;

/// <summary>
///     One connection to <c>LocalChatHub</c>: streams a send or a cold re-attach as <see cref="CliStreamEvent" />s that
///     always end in a terminal kind, repairs gaps through <c>ResumeMessage</c>, and cancels over REST.
/// </summary>
/// <remarks>
///     Mirrors the SPA's <c>NodeChatStreamTransport.ts</c>. When a stream cannot be repaired (an unknown or finished
///     invocation, a replay cap, three failed repairs) the persisted message is read over REST and its status becomes
///     the terminal event, so the caller never has to refetch on its own.
/// </remarks>
internal sealed class ChatSession : IAsyncDisposable
{
    internal const string HubPath = "chat/hub";

    private const int MaxRepairsPerStream = 3;

    private readonly HubConnection _connection;
    private readonly NodeApiClient _api;
    private readonly TimeProvider _timeProvider;

    internal ChatSession(HubConnection connection, NodeApiClient api, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _connection = connection;
        _api = api;
        _timeProvider = timeProvider;
    }

    private enum StreamEnd
    {
        Repair,
        Settle
    }

    /// <summary>Opens the hub connection and returns once the node has registered it.</summary>
    public static async Task<ChatSession> ConnectAsync(NodeConnectionOptions options,
        NodeAuthClient auth,
        NodeApiClient api,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA2000 // Ownership moves to the returned session; a failed start disposes it below.
        var connection = CreateConnection(options, auth);
#pragma warning restore CA2000
        try
        {
            await StartAndAwaitRegistrationAsync(connection, cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new ChatSession(connection, api, timeProvider);
    }

    internal static HubConnection CreateConnection(NodeConnectionOptions options, NodeAuthClient auth)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(auth);

        var certificateValidation = NodeHttp.CertificateValidation(options);
        return new HubConnectionBuilder()
               .WithUrl(new Uri(options.Origin, NodeHttp.ApiPrefix + HubPath), http =>
               {
                   http.AccessTokenProvider = async () => await auth.GetAccessTokenAsync(CancellationToken.None);
                   if (options.HandlerFactory is not null || certificateValidation is not null)
                   {
                       // SignalR disposes the handler it is given with the connection, so each call returns a new one.
                       http.HttpMessageHandlerFactory = inner =>
                       {
                           inner.Dispose();
                           return options.HandlerFactory?.Invoke() ?? NodeHttp.CreateSocketsHandler(options);
                       };
                   }

                   if (certificateValidation is not null)
                   {
                       http.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = certificateValidation;
                   }
               })
               .WithAutomaticReconnect()
               .AddJsonProtocol(json => NodeHttp.ConfigureJson(json.PayloadSerializerOptions))
               .Build();
    }

    /// <summary>Sends a turn; <see cref="NodeChatStreamRequest.MessageId" /> and <see cref="NodeChatStreamRequest.RequestId" /> are minted when absent.</summary>
    public IAsyncEnumerable<CliStreamEvent> SendAsync(NodeChatStreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var send = request with
        {
            MessageId = request.MessageId ?? Guid.NewGuid(),
            RequestId = request.RequestId ?? Guid.NewGuid()
        };
        return RunAsync("SendMessage", [send], new ChatStreamFolder(send.ConversationId, send.MessageId, send.RequestId), openingIsResume: false, cancellationToken);
    }

    /// <summary>Re-attaches to whatever turn is live in the conversation, prompts included; empty when nothing is live.</summary>
    public IAsyncEnumerable<CliStreamEvent> ResumeConversationAsync(Guid conversationId, CancellationToken cancellationToken) =>
        RunAsync("ResumeConversation", [conversationId], new ChatStreamFolder(conversationId), openingIsResume: true, cancellationToken);

    /// <summary>Stops the turn <paramref name="turn" /> belongs to; the stream then ends with its cancelled terminal.</summary>
    public Task<CancelMessageResponse> CancelAsync(CliStreamEvent turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);

        return _api.CancelAsync(new CancelMessageRequest
        {
            ConversationId = turn.ConversationId,
            MessageId = turn.MessageId,
            RequestId = turn.RequestId
        }, cancellationToken);
    }

    public ValueTask DisposeAsync() =>
        _connection.DisposeAsync();

    private static async Task StartAndAwaitRegistrationAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        await connection.StartAsync(cancellationToken);
        try
        {
            // The node reads client frames only after it registered the connection, so its refusal proves registration.
            await connection.InvokeAsync("RegistrationProbe", cancellationToken);
        }
        catch (HubException)
        {
            // Expected: the reply to a method that does not exist.
        }
    }

    private static async Task<StreamRead> ReadAsync(IAsyncEnumerator<ChatStreamEvent> frames)
    {
        try
        {
            return await frames.MoveNextAsync() ? new StreamRead(frames.Current, Error: null) : new StreamRead(Frame: null, Error: null);
        }
        catch (Exception exception)
        {
            return new StreamRead(Frame: null, exception);
        }
    }

    private async IAsyncEnumerable<CliStreamEvent> RunAsync(string method,
        object?[] arguments,
        ChatStreamFolder folder,
        bool openingIsResume,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var watchdog = new ChatStreamWatchdog();
        var isResume = openingIsResume;
        var resolveMessageId = folder.MessageId is null;
        var repairs = 0;
        while (true)
        {
            var end = StreamEnd.Settle;
            using (var deadline = new CancellationTokenSource())
                using (var subscription = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
#pragma warning disable MA0045 // A TimerCallback is synchronous by contract.
                    await using (var watchdogTimer = _timeProvider.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(),
                                     deadline,
                                     Timeout.InfiniteTimeSpan,
                                     Timeout.InfiniteTimeSpan))
#pragma warning restore MA0045
                    {
                        var frames = _connection.StreamAsyncCore<ChatStreamEvent>(method, arguments, subscription.Token).GetAsyncEnumerator(subscription.Token);
                        var foldedAny = false;
                        try
                        {
                            while (true)
                            {
                                watchdogTimer.Change(watchdog.NextDeadline, Timeout.InfiniteTimeSpan);
                                var read = await ReadAsync(frames);
                                watchdogTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                                cancellationToken.ThrowIfCancellationRequested();
                                if (deadline.IsCancellationRequested)
                                {
                                    throw new ChatStreamWatchdogException(watchdog.AwaitingFirstFrame);
                                }

                                if (read.Frame is { } frame)
                                {
                                    watchdog.Observe(frame);
                                    if (resolveMessageId)
                                    {
                                        resolveMessageId = false;
                                        folder.MessageId = await FindAssistantMessageIdAsync(frame, cancellationToken);
                                    }

                                    var outcome = folder.Fold(frame);

                                    // A resume whose FIRST frame is a reconcile is the replay cap: re-entering would earn the same
                                    // answer, so settle from the persisted message, whose ids the fold above latched.
                                    if (isResume && !foldedAny && frame.Type == ChatStreamEventTypes.AssistantReconcile)
                                    {
                                        break;
                                    }

                                    foldedAny = true;
                                    if (outcome.Event is { } surfaced)
                                    {
                                        yield return surfaced;
                                    }

                                    if (folder.IsTerminal)
                                    {
                                        yield break;
                                    }

                                    if (outcome.NeedsResume)
                                    {
                                        end = StreamEnd.Repair;
                                        break;
                                    }

                                    continue;
                                }

                                if (read.Error is null || (read.Error is HubException && isResume))
                                {
                                    break;
                                }

                                if (read.Error is HubException || folder.InvocationId is null || !await WaitForReconnectAsync(cancellationToken))
                                {
                                    ExceptionDispatchInfo.Throw(read.Error);
                                }

                                end = StreamEnd.Repair;
                                break;
                            }
                        }
                        finally
                        {
                            await subscription.CancelAsync();
                            await frames.DisposeAsync();
                        }
                    }

            if (end == StreamEnd.Repair && folder.InvocationId is { } invocationId && ++repairs <= MaxRepairsPerStream)
            {
                method = "ResumeMessage";
                arguments = [invocationId];
                isResume = true;
                continue;
            }

            if (await SettleFromPersistedMessageAsync(folder, cancellationToken) is { } settled)
            {
                yield return settled;
            }

            yield break;
        }
    }

    // A cold re-attach stamps the invocation id as the message id; the persisted assistant row carries the real one.
    private async Task<Guid?> FindAssistantMessageIdAsync(ChatStreamEvent frame, CancellationToken cancellationToken)
    {
        var conversation = await _api.GetConversationAsync(frame.ConversationId, cancellationToken);
        return conversation.Messages.LastOrDefault(message => message.RequestId == frame.RequestId && IsAssistant(message))?.MessageId;
    }

    private async Task<CliStreamEvent?> SettleFromPersistedMessageAsync(ChatStreamFolder folder, CancellationToken cancellationToken)
    {
        if (folder.ConversationId is not { } conversationId || (folder.MessageId is null && folder.InvocationId is null))
        {
            return null;
        }

        var conversation = await _api.GetConversationAsync(conversationId, cancellationToken);
        var message = conversation.Messages.LastOrDefault(candidate => candidate.MessageId == folder.MessageId)
                      ?? conversation.Messages.LastOrDefault(candidate => candidate.RequestId == folder.InvocationId && IsAssistant(candidate));
        var type = message?.Status switch
        {
            "completed" => ChatStreamEventTypes.AssistantCompleted,
            "cancelled" => ChatStreamEventTypes.AssistantCancelled,
            "failed" => ChatStreamEventTypes.AssistantFailed,
            _ => ChatStreamEventTypes.AssistantInterrupted
        };
        var error = message switch
        {
            null => "The turn could not be found on the node.",
            { Status: "completed" or "cancelled" or "failed" or "interrupted" } => message.Error,
            _ => "The stream was lost while the turn is still running on the node."
        };
        return folder.Fold(new ChatStreamEvent
        {
            Type = type,
            ConversationId = conversationId,
            MessageId = folder.MessageId ?? message?.MessageId ?? Guid.Empty,
            RequestId = folder.InvocationId ?? message?.RequestId ?? Guid.Empty,
            Status = message?.Status ?? "interrupted",
            Content = message?.Content ?? folder.Content,
            Reasoning = message?.Reasoning ?? folder.Reasoning,
            Error = error,
            Model = message?.Model
        }).Event;
    }

    private static bool IsAssistant(ChatMessage message) =>
        string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase);

    // The SPA's onReconnected path: wait out the automatic reconnect, then the caller resumes by invocation id.
    private async Task<bool> WaitForReconnectAsync(CancellationToken cancellationToken)
    {
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<string?, Task> onReconnected = _ =>
        {
            outcome.TrySetResult(true);
            return Task.CompletedTask;
        };
        Func<Exception?, Task> onClosed = _ =>
        {
            outcome.TrySetResult(false);
            return Task.CompletedTask;
        };
        _connection.Reconnected += onReconnected;
        _connection.Closed += onClosed;
        try
        {
            return _connection.State switch
            {
                HubConnectionState.Connected => true,
                HubConnectionState.Disconnected => false,
                _ => await outcome.Task.WaitAsync(cancellationToken)
            };
        }
        finally
        {
            _connection.Reconnected -= onReconnected;
            _connection.Closed -= onClosed;
        }
    }

    private readonly record struct StreamRead(ChatStreamEvent? Frame, Exception? Error);
}
