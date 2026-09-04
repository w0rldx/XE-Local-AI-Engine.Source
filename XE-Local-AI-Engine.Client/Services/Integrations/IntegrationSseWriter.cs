namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Buffers;
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

/// <summary>What the writer decided, so the route can pick a status. There is no 404 here: the writer has no store.</summary>
internal enum IntegrationSseWriteOutcome
{
    /// <summary>The 200, the headers and the frames are already on the wire; the route has nothing left to write.</summary>
    Streamed,

    /// <summary>The ring cannot serve this cursor. The route answers 410 and names the persisted-events route.</summary>
    Gone,

    /// <summary>Too many streams are already open. The route answers 503 with a <c>Retry-After</c>.</summary>
    Busy
}

/// <summary>
///     Frames an execution's events onto the response as <c>text/event-stream</c>.
///     <para>
///         <b>Every refusal happens before a byte is written.</b> ASP.NET Core cannot change a status once the response
///         has started, so deciding 410 by starting the enumerator — and catching the gap at the first
///         <c>MoveNextAsync</c> — would reset the connection instead of answering it. That is exactly the failure the
///         410-then-poll contract exists to avoid.
///     </para>
///     <para>
///         <b>The caller's token ends forwarding and nothing else.</b> It is never linked to the run: an integrator
///         that closes its stream to poll instead must not thereby cancel a generation the node is paying for.
///     </para>
/// </summary>
internal sealed class IntegrationSseWriter : IDisposable
{
    private const int KeepaliveSeconds = 15;

    private static readonly byte[] KeepaliveFrame = Encoding.UTF8.GetBytes(": keepalive\n\n");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IIntegrationExecutionEventBuffer _buffer;

    private readonly ILogger<IntegrationSseWriter> _logger;

    /// <summary>
    ///     Concurrent open streams, bounded by the SAME option that bounds tracked executions. Many readers can attach
    ///     to one execution, so the buffer's cap bounds executions and the fixed-window limiter bounds attach RATE;
    ///     neither bounds concurrency, which is what this does.
    /// </summary>
    private readonly SemaphoreSlim _openStreams;

    private readonly TimeProvider _timeProvider;

    public IntegrationSseWriter(IIntegrationExecutionEventBuffer buffer,
        IOptions<IntegrationOptions> options,
        TimeProvider timeProvider,
        ILogger<IntegrationSseWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var maxStreams = options.Value.MaxTrackedExecutions;
        _openStreams = new SemaphoreSlim(maxStreams, maxStreams);
    }

    public void Dispose() =>
        _openStreams.Dispose();

    public async Task<IntegrationSseWriteOutcome> WriteAsync(HttpContext context,
        Guid executionId,
        long sinceSequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Step 0. Non-blocking admission: a caller that cannot be served now is told so, rather than parked holding a
        // connection until one of the sixty-four ahead of it finishes.
        // TimeSpan.Zero, and CancellationToken.None on purpose: this is a try-acquire, not a wait, so there is nothing
        // for the caller's token to cancel.
        if (!await _openStreams.WaitAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false))
        {
            return IntegrationSseWriteOutcome.Busy;
        }

        try
        {
            // Step 1. The pre-header precheck. IsTracked is what separates "no entry" from "an entry that has dropped
            // nothing": on the numbers alone both read (0, 0).
            if (!_buffer.IsTracked(executionId))
            {
                return IntegrationSseWriteOutcome.Gone;
            }

            // Floor names the OLDEST RETAINED sequence and sinceSequence is exclusive, so a caller sitting at Floor - 1
            // is served losslessly and the ordinary first attach (since 0, floor 1) streams.
            var floor = _buffer.Floor(executionId);
            var head = _buffer.LastSequence(executionId);
            if (sinceSequence < floor - 1 || sinceSequence > head)
            {
                return IntegrationSseWriteOutcome.Gone;
            }

            await StreamAsync(context, executionId, sinceSequence, cancellationToken).ConfigureAwait(false);
            return IntegrationSseWriteOutcome.Streamed;
        }
        finally
        {
            _ = _openStreams.Release();
        }
    }

    private static void WriteJson(SseItem<IntegrationStreamEvent> item, IBufferWriter<byte> writer)
    {
        using var json = new Utf8JsonWriter(writer);
        // Compact JSON never contains a raw newline, so `data:` is always exactly one line whatever a payload holds.
        JsonSerializer.Serialize(json, item.Data, JsonOptions);
    }

    private static async IAsyncEnumerable<SseItem<IntegrationStreamEvent>> OneAsync(IntegrationStreamEvent streamEvent)
    {
        // One formatter call per event, because the BCL formatter owns the loop and cannot emit the ": keepalive"
        // comment the contract requires. It still owns framing, data: escaping and the per-event flush.
        await Task.CompletedTask.ConfigureAwait(false);
        yield return new SseItem<IntegrationStreamEvent>(streamEvent, streamEvent.Type)
        {
            EventId = streamEvent.Sequence.ToString(CultureInfo.InvariantCulture)
        };
    }

    private async Task StreamAsync(HttpContext context, Guid executionId, long sinceSequence, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        // Commit the headers before the first event, so a caller sees the 200 immediately rather than at the first
        // frame — which on a cold model load can be minutes away.
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        // The reader gets OUR token, linked to the caller's. A write failure that is not an abort — a dead peer
        // surfacing as IOException — leaves the caller's token uncancelled, and the outstanding move would then park
        // forever; cancelling this one is what bounds the drain in the finally.
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readToken = readCancellation.Token;
        var source = _buffer.ReadAsync(executionId, sinceSequence, readToken).GetAsyncEnumerator(readToken);
        Task<bool>? pending = null;
        try
        {
            while (true)
            {
                // ONE pending move, held across any number of keepalives. MoveNextAsync may be called only once at a
                // time on an enumerator, and re-issuing it after a timeout would drop the event the first call is about
                // to return.
                pending ??= source.MoveNextAsync().AsTask();

                using var keepaliveCancellation = CancellationTokenSource.CreateLinkedTokenSource(readToken);
                var keepalive = Task.Delay(TimeSpan.FromSeconds(KeepaliveSeconds), _timeProvider, keepaliveCancellation.Token);
                if (await Task.WhenAny(pending, keepalive).ConfigureAwait(false) != pending)
                {
                    // A comment, not an event: an EventSource ignores it in silence, where an `event: keepalive` frame
                    // would reach every listener and change the external contract.
                    await context.Response.Body.WriteAsync(KeepaliveFrame, cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await keepaliveCancellation.CancelAsync().ConfigureAwait(false);
                if (!await pending.ConfigureAwait(false))
                {
                    return;
                }

                await SseFormatter.WriteAsync(OneAsync(source.Current), context.Response.Body, WriteJson, cancellationToken).ConfigureAwait(false);
                pending = null;
            }
        }
        catch (IntegrationEventGapException)
        {
            // The status is already on the wire, so this cannot become a 410. End the response cleanly and write no
            // frame: none of the eleven locked event types means "you were cut". The caller re-attaches with
            // Last-Event-ID and THAT attach answers 410 with the recovery route.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                          || (exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // The caller went away: an abort, a dead peer Kestrel surfaces as IOException or a connection torn down
            // under the write. Forwarding stops; the run does not. The proxy forwarder swallows the same family for
            // the same reason, and on a response that already sent 200 there is no status left to say it with.
            _logger.LogDebug(exception, "The integration event stream for execution {ExecutionId} ended early.", executionId);
        }
        finally
        {
            // NEVER dispose the enumerator with a move in flight: a compiler-generated async iterator answers that with
            // NotSupportedException, thrown outside every catch above and onto a response that already sent its 200.
            // Cancelling our own token ends the reader's wait, so the drain is bounded by us and not by the peer.
            if (pending is { IsCompleted: false })
            {
                await readCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    _ = await pending.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Whatever the abandoned move ended as, nothing can act on it: the response is over.
                    _logger.LogDebug(exception, "The integration event reader for execution {ExecutionId} ended while being drained.", executionId);
                }
            }

            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
