namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A user Stop mid-stream reaches MEAI's chat telemetry hop as an <see cref="OperationCanceledException" /> on its
///     <c>gen_ai.client.operation.exception</c> event: the exact shape the node's log filter drops.
/// </summary>
/// <remarks>
///     The filter's premise: an SDK or MEAI upgrade that changes the exception type or event name turns this red first.
///     Driven through the real <see cref="DeferredLlamaServerChatClient" /> and its OpenAI adapter against a loopback
///     <see cref="HttpListener" /> that sends one chunk and then stalls, as llama-server does mid-generation.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class DeferredLlamaServerStopTelemetryTests
{
    private const string FirstChunk =
        "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"qwen3:8b\","
        + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"partial\"},\"finish_reason\":null}]}\n\n";

    [Test]
    public async Task UserStopMidStream_LogsTheOperationExceptionEventWithACancellation()
    {
        using var server = await StallingServer.StartAsync();
        var records = new List<LoggedRecord>();
        using var loggerFactory = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new RecordingProvider(records)));
        using var deferred = new DeferredLlamaServerChatClient(new FakeProcessSupervisor
            {
                EnsureEndpoint = server.BaseAddress
            },
            "qwen3:8b",
            TimeSpan.FromSeconds(30));
        using var client = deferred.AsBuilder().UseOpenTelemetry(loggerFactory, sourceName: "Microsoft.Extensions.AI").Build();
        using var stop = new CancellationTokenSource();

        await using (var enumerator = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, stop.Token).GetAsyncEnumerator(stop.Token))
        {
            AssertEx.True(await enumerator.MoveNextAsync(), "the stub's first chunk must arrive before the stop.");
            await stop.CancelAsync();
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        }

        LoggedRecord[] logged;
        lock (records)
        {
            logged = [.. records.Where(static record => record.Exception is not null)];
        }

        var record = AssertEx.NotNull(logged.SingleOrDefault(), $"exactly one exception event must be logged, got {logged.Length}.");
        AssertEx.Equal("Microsoft.Extensions.AI.OpenTelemetryChatClient", record.Category);
        AssertEx.Equal("gen_ai.client.operation.exception", record.EventId.Name);
        AssertEx.True(record.Exception is OperationCanceledException, $"the stop must reach the telemetry hop as a cancellation, got {record.Exception}.");
    }

    private sealed class LoggedRecord
    {
        public required string Category { get; init; }

        public required EventId EventId { get; init; }

        public Exception? Exception { get; init; }
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        private readonly List<LoggedRecord> _records;

        public RecordingProvider(List<LoggedRecord> records)
        {
            _records = records;
        }

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(categoryName, _records);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _category;
        private readonly List<LoggedRecord> _records;

        public RecordingLogger(string category, List<LoggedRecord> records)
        {
            _category = category;
            _records = records;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_records)
            {
                _records.Add(new LoggedRecord
                {
                    Category = _category,
                    EventId = eventId,
                    Exception = exception
                });
            }
        }
    }

    /// <summary>A loopback server that answers one streaming request with a single chunk and then holds the response open.</summary>
    private sealed class StallingServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        // Never completed: only disposal ends the held response.
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private StallingServer(HttpListener listener, Uri baseAddress)
        {
            _listener = listener;
            BaseAddress = baseAddress;
        }

        public Uri BaseAddress { get; }

        public static async Task<StallingServer> StartAsync()
        {
            // HttpListener has no "bind port 0" mode: bind an OS-chosen candidate, and take a fresh one when another process claimed it first.
            var server = await LoopbackPort.BindWithRetryAsync(static port =>
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                    return Task.FromResult<StallingServer?>(null);
                }

                return Task.FromResult<StallingServer?>(new StallingServer(listener, new Uri($"http://127.0.0.1:{port}/v1")));
            });
            server.Serve();
            return server;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }

        private void Serve()
        {
            _ = Task.Run(async () =>
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        context.Response.StatusCode = (int)HttpStatusCode.OK;
                        context.Response.ContentType = "text/event-stream";
                        context.Response.SendChunked = true;
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(FirstChunk), _cts.Token);
                        await context.Response.OutputStream.FlushAsync(_cts.Token);
                        // Mid-generation: no further chunk until the test ends.
                        await _held.Task.WaitAsync(_cts.Token);
                    }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or OperationCanceledException)
                    {
                        // Disposed.
                    }
                },
                _cts.Token);
        }
    }
}
