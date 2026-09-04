namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Net.Http.Headers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     The three hand-mapped external integration routes: invoke, status and cancel. The analogue of
///     <c>LocalModelProxyForwarder</c> — a request/response contract an external caller owns, deliberately outside
///     FastEndpoints so it never reaches the OpenAPI document or the generated React client.
///     <para>
///         <b>There is no 403 on this family.</b> A missing, malformed, unknown or revoked credential is the
///         authentication handler's 401. Everything authorisation-shaped — a trigger this key may not fire, an
///         execution belonging to another integrator, an execution under a trigger this key is not scoped to — is a
///         404 byte-identical to "unknown". The id is the capability, so a row a caller may not see must not be
///         confirmable.
///     </para>
/// </summary>
internal sealed class IntegrationApiHandler
{
    private const string EventStreamMediaType = "text/event-stream";

    private const string MaskedExecutionMessage = "No such execution.";

    /// <summary>The session family's single masked answer. Unknown, foreign and allowlist-excluded are all this one.</summary>
    private const string MaskedSessionMessage = "No such session.";

    /// <summary>
    ///     The machine-readable half of the two session refusals a caller can actually act on: retry after the running
    ///     execution ends, or start a new session. Prose alone made an integrator match on the sentence.
    ///     <para>
    ///         NOT members of <c>IntegrationFailureCategories</c>: nothing here failed a RUN, and that vocabulary is
    ///         asserted closed. The masked 404 and the 401 stay code-free — a discriminator there is the leak the whole
    ///         family is shaped to avoid.
    ///     </para>
    /// </summary>
    private const string SessionBusyCode = "session-busy";

    private const string SessionClosedCode = "session-closed";

    private static readonly JsonSerializerOptions RequestSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IntegrationExternalAccess _access;
    private readonly IIntegrationInvocationService _invocations;
    private readonly IntegrationPrincipalRateLimiter _rateLimiter;
    private readonly IntegrationExecutionQueryService _executions;
    private readonly IntegrationSessionService _sessions;
    private readonly IntegrationSseWriter _writer;

    public IntegrationApiHandler(IIntegrationInvocationService invocations,
        IntegrationExternalAccess access,
        IntegrationExecutionQueryService executions,
        IntegrationSessionService sessions,
        IntegrationSseWriter writer,
        IntegrationPrincipalRateLimiter rateLimiter)
    {
        _invocations = invocations ?? throw new ArgumentNullException(nameof(invocations));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
    }

    /// <summary>
    ///     The integrator's own session status.
    ///     <para>
    ///         Its ENTIRE authorisation decision is <c>IntegrationSessionService.GetForExternalCallerAsync</c>, which is
    ///         the shared access helper and nothing else: unknown, owned by another principal, and belonging to a
    ///         trigger this key's allowlist excludes all come back <see langword="null" /> and map to ONE 404 with a
    ///         byte-identical body. No masking is assembled here, because separate <c>if</c>s in a handler are separate
    ///         chances to return a distinguishable answer.
    ///     </para>
    /// </summary>
    public async Task GetSessionAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var caller = await AuthorizeAsync(context).ConfigureAwait(false);
        if (caller is null)
        {
            return;
        }

        if (!TryReadGuidRoute(context, "sessionId", out var sessionId))
        {
            await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedSessionMessage).ConfigureAwait(false);
            return;
        }

        var session = await _sessions.GetForExternalCallerAsync(sessionId, caller, context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedSessionMessage).ConfigureAwait(false);
            return;
        }

        await context.Response.WriteAsJsonAsync(new IntegrationSessionStatusResponse(session.Id,
                session.TriggerName,
                SessionStatusName(session.Status),
                session.ExecutionCount,
                session.LastActivityUtc),
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    ///     The wire spelling of a session status, an explicit map for the same reason the execution one is: these
    ///     strings are the external contract a caller branches on, so a renamed enum member must break the build here.
    /// </summary>
    private static string SessionStatusName(IntegrationSessionStatus status) =>
        status switch
        {
            IntegrationSessionStatus.Active => "active",
            IntegrationSessionStatus.Closed => "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown integration session status.")
        };

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var caller = await AuthorizeAsync(context).ConfigureAwait(false);
        if (caller is null)
        {
            return;
        }

        // The body cap has exactly ONE source: the value baked onto the route at composition time. A per-request
        // IOptions instance would be a second authority that can disagree with the metadata the route already carries.
        var cap = context.GetEndpoint()?.Metadata.GetMetadata<IntegrationRequestSizeLimit>()?.MaxRequestBodySize
                  ?? throw new InvalidOperationException("The integration invoke route is missing its request-size metadata.");

        // Mechanism 2: raise or lower the HOST cap before the body is touched. This is what covers a CHUNKED body,
        // which has no Content-Length to inspect at all. Kestrel enforces it as the body is consumed; the feature
        // rejects a set once reading has started, and this handler is not the only thing that may have touched the
        // request.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = cap;
        }

        // A cheap early exit, never the limit: Content-Length is never trusted alone.
        if (context.Request.ContentLength > cap)
        {
            await WriteMessageAsync(context, StatusCodes.Status413PayloadTooLarge, "The request body is larger than this node accepts.").ConfigureAwait(false);
            return;
        }

        byte[] body;
        try
        {
            // Mechanism 3: the bounded read. The only one that works where no body-size feature exists at all, and the
            // only one provable without a real Kestrel connection.
            var read = await ReadBoundedBodyAsync(context, cap).ConfigureAwait(false);
            if (read is null)
            {
                await WriteMessageAsync(context, StatusCodes.Status413PayloadTooLarge, "The request body is larger than this node accepts.").ConfigureAwait(false);
                return;
            }

            body = read;
        }
        catch (BadHttpRequestException)
        {
            // Mechanism 2 firing: Kestrel refused the body as it was consumed.
            await WriteMessageAsync(context, StatusCodes.Status413PayloadTooLarge, "The request body is larger than this node accepts.").ConfigureAwait(false);
            return;
        }

        IntegrationInvokeRequest? parsed;
        try
        {
            // Parsed from the SAME buffer the fingerprint is taken over — never from a second read, which could not be
            // guaranteed byte-identical.
            parsed = JsonSerializer.Deserialize<IntegrationInvokeRequest>(body, RequestSerializerOptions);
        }
        catch (JsonException)
        {
            // A distinct failure with a distinct code: malformed is 400, oversized is 413.
            await WriteMessageAsync(context, StatusCodes.Status400BadRequest, "The request body is not valid JSON.").ConfigureAwait(false);
            return;
        }

        if (parsed?.RequestId is not { } requestId || requestId == Guid.Empty)
        {
            await WriteMessageAsync(context, StatusCodes.Status400BadRequest, "Send a requestId so a retry can be recognised.").ConfigureAwait(false);
            return;
        }

        if (!TryMapInputs(parsed.Inputs, out var inputs))
        {
            await WriteMessageAsync(context, StatusCodes.Status422UnprocessableEntity, "Each input must name a supported kind and carry its content.").ConfigureAwait(false);
            return;
        }

        var triggerName = context.Request.RouteValues.TryGetValue("triggerName", out var routeValue) ? routeValue as string : null;
        var result = await _invocations.AcceptAsync(new IntegrationAcceptRequest(triggerName ?? string.Empty,
                                               caller.PrincipalId,
                                               caller.KeyPrefix,
                                               requestId,
                                               parsed.SessionId,
                                               inputs,
                                               body),
                                           context.RequestAborted)
                                       .ConfigureAwait(false);

        // The stream is offered only for an admitted execution. Every rejection above answered with a real status on a
        // response that has not started, which is the property that lets a 503 or a 409 still be JSON even when the
        // caller asked for a stream.
        // A refusal here is NOT the accept's answer. The accept transaction has already committed, so answering 503
        // ("not admitted") or 410 would contradict an execution that is running and holding the node's lease: fall
        // through to the ordinary accept body, which names the execution and the events route to attach to instead.
        // Only the GET route, where a refusal really is the whole answer, maps an outcome to a status.
        if (result.Outcome is IntegrationAcceptOutcome.Accepted or IntegrationAcceptOutcome.Duplicate
            && WantsEventStream(context)
            && result.ExecutionId is { } admitted
            && await _writer.WriteAsync(context, admitted, sinceSequence: 0, context.RequestAborted).ConfigureAwait(false)
            == IntegrationSseWriteOutcome.Streamed)
        {
            return;
        }

        await WriteAcceptOutcomeAsync(context, result).ConfigureAwait(false);
    }

    /// <summary>
    ///     The live stream, resumable through <c>Last-Event-ID</c>. Authorisation runs FIRST and in full — principal and
    ///     the current key's trigger allowlist, through the one shared helper — so a caller that may not see this
    ///     execution gets the masked 404 before any header is written, and never a partially written stream.
    /// </summary>
    public async Task GetExecutionEventsAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var caller = await AuthorizeAsync(context).ConfigureAwait(false);
        if (caller is null)
        {
            return;
        }

        if (!TryReadExecutionId(context, out var executionId))
        {
            await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedExecutionMessage).ConfigureAwait(false);
            return;
        }

        var access = await _access.ResolveExecutionAsync(executionId, caller, context.RequestAborted).ConfigureAwait(false);
        if (access.Outcome == IntegrationAccessOutcome.Masked)
        {
            await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedExecutionMessage).ConfigureAwait(false);
            return;
        }

        if (WantsEventStream(context))
        {
            await WriteStreamAsync(context, executionId, ReadLastEventId(context)).ConfigureAwait(false);
            return;
        }

        // The persisted rows: the same route, the same masking, the database rather than the ring. It is what a caller
        // answered 410 on the stream falls back to, so it never answers 410 itself — after a restart the ring is empty
        // and the rows are not.
        var rows = await _executions.ListEventsAsync(executionId,
                                       Math.Max(ReadLong(context, "sinceSeq"), val2: 0),
                                       IntegrationEventPage.ClampLimit(ReadLimit(context)),
                                       context.RequestAborted)
                                   .ConfigureAwait(false);

        await context.Response.WriteAsJsonAsync(rows.Select(IntegrationMapper.ToEventDto).ToArray(), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>A query value as a long, or 0. Never arithmetic on a sequence — holes make counting meaningless.</summary>
    private static long ReadLong(HttpContext context, string name) =>
        long.TryParse(context.Request.Query[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int? ReadLimit(HttpContext context) =>
        int.TryParse(context.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) ? limit : null;

    /// <summary>
    ///     Hands the response to the writer and maps its answer. GET only: on the invoke route the execution is already
    ///     admitted, so a refusal falls through to the accept body instead. The writer refuses before any byte is
    ///     written, which is what keeps 410 and 503 real statuses rather than a reset connection.
    /// </summary>
    private async Task WriteStreamAsync(HttpContext context, Guid executionId, long sinceSequence)
    {
        var outcome = await _writer.WriteAsync(context, executionId, sinceSequence, context.RequestAborted).ConfigureAwait(false);
        switch (outcome)
        {
            case IntegrationSseWriteOutcome.Gone:
                await WriteMessageAsync(context,
                        StatusCodes.Status410Gone,
                        $"The live stream no longer holds this position. Read the committed events from {EventsPath(executionId)} and the status from {SelfPath(executionId)}.")
                    .ConfigureAwait(false);
                return;
            case IntegrationSseWriteOutcome.Busy:
                context.Response.Headers.RetryAfter = "5";
                await WriteMessageAsync(context, StatusCodes.Status503ServiceUnavailable, "Too many streams are open on this node. Try again shortly.").ConfigureAwait(false);
                return;
            default:
                // Streamed: the 200, the headers and every frame are already on the wire.
                return;
        }
    }

    /// <summary>
    ///     <c>Last-Event-ID</c> as a WATERMARK: a non-negative long, and anything else is a fresh attach from zero. It
    ///     is never arithmetic — holes are legal, so the value is only ever compared.
    /// </summary>
    private static long ReadLastEventId(HttpContext context) =>
        context.Request.Headers.TryGetValue("Last-Event-ID", out var raw)
        && long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
        && sequence >= 0
            ? sequence
            : 0;

    /// <summary>
    ///     Whether the caller asked for the stream, PARSED rather than substring-matched: <c>text/event-stream;q=0</c>
    ///     is a refusal, not a request, and <c>*/*</c> — curl's default — asks for the node's own default, which on
    ///     this family is JSON.
    /// </summary>
    private static bool WantsEventStream(HttpContext context) =>
        MediaTypeHeaderValue.TryParseList(context.Request.Headers.Accept, out var accepted)
        && accepted.Any(static media => media.MediaType.Equals(EventStreamMediaType, StringComparison.OrdinalIgnoreCase) && media.Quality != 0);

    public async Task GetExecutionAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var caller = await AuthorizeAsync(context).ConfigureAwait(false);
        if (caller is null)
        {
            return;
        }

        if (!TryReadExecutionId(context, out var executionId))
        {
            await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedExecutionMessage).ConfigureAwait(false);
            return;
        }

        var access = await _access.ResolveExecutionAsync(executionId, caller, context.RequestAborted).ConfigureAwait(false);
        if (access.Outcome == IntegrationAccessOutcome.Masked || access.Execution is not { } execution)
        {
            await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedExecutionMessage).ConfigureAwait(false);
            return;
        }

        await context.Response.WriteAsJsonAsync(new IntegrationExecutionStatusResponse(execution.Id,
                execution.SessionId,
                StatusName(execution.Status),
                execution.FailureCategory,
                execution.FailureSummary,
                execution.ReceivedAtUtc,
                execution.StartedAtUtc,
                execution.EndedAtUtc,
                execution.OutputCount,
                Links(execution.Id)),
            context.RequestAborted).ConfigureAwait(false);
    }

    public async Task CancelExecutionAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var caller = await AuthorizeAsync(context).ConfigureAwait(false);
        if (caller is null)
        {
            return;
        }

        if (!TryReadExecutionId(context, out var executionId))
        {
            await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedExecutionMessage).ConfigureAwait(false);
            return;
        }

        // Authorisation, not a read: the masked cancel must leave no trace at all, so this runs BEFORE the marker is
        // stamped.
        var access = await _access.ResolveExecutionAsync(executionId, caller, context.RequestAborted).ConfigureAwait(false);
        if (access.Outcome == IntegrationAccessOutcome.Masked)
        {
            await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedExecutionMessage).ConfigureAwait(false);
            return;
        }

        var outcome = await _executions.RequestCancelAsync(executionId, context.RequestAborted).ConfigureAwait(false);
        switch (outcome)
        {
            case IntegrationCancelOutcome.Requested:
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Cancellation requested."
                }, context.RequestAborted).ConfigureAwait(false);
                return;
            case IntegrationCancelOutcome.AlreadyTerminal:
                await WriteMessageAsync(context, StatusCodes.Status409Conflict, "The execution has already finished.").ConfigureAwait(false);
                return;
            default:
                await WriteMessageAsync(context, StatusCodes.Status404NotFound, MaskedExecutionMessage).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    ///     The two guards every route opens with, in this order. Identity first, because there is nothing to partition
    ///     on before it; then the per-principal budget, BEFORE any store read and before the body is read, so an
    ///     oversized body from a principal already over its budget costs one dictionary lookup rather than a megabyte
    ///     of buffered reads. It runs on all three routes: a caller must not be able to dodge it by polling status in a
    ///     loop instead of invoking.
    /// </summary>
    private async Task<IntegrationCallerIdentity?> AuthorizeAsync(HttpContext context)
    {
        var caller = IntegrationCallerIdentity.FromPrincipal(context.User);
        if (caller is null)
        {
            await WriteUnauthorizedAsync(context).ConfigureAwait(false);
            return null;
        }

        if (!_rateLimiter.TryAcquire(caller.PrincipalId.ToString("D")))
        {
            context.Response.Headers.RetryAfter = "60";
            await WriteMessageAsync(context, StatusCodes.Status429TooManyRequests, "Too many integration requests. Try again shortly.").ConfigureAwait(false);
            return null;
        }

        return caller;
    }

    private static async Task WriteAcceptOutcomeAsync(HttpContext context, IntegrationAcceptResult result)
    {
        switch (result.Outcome)
        {
            case IntegrationAcceptOutcome.Accepted:
            case IntegrationAcceptOutcome.Duplicate:
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                await context.Response.WriteAsJsonAsync(new IntegrationAcceptResponse(result.ExecutionId!.Value,
                        result.SessionId!.Value,
                        StatusName(result.Status ?? IntegrationExecutionStatus.Accepted),
                        Links(result.ExecutionId.Value)),
                    context.RequestAborted).ConfigureAwait(false);
                return;
            case IntegrationAcceptOutcome.TriggerNotFound:
            case IntegrationAcceptOutcome.SessionNotFound:
                await WriteMessageAsync(context, StatusCodes.Status404NotFound, result.Message).ConfigureAwait(false);
                return;
            case IntegrationAcceptOutcome.RequestConflict:
                await WriteMessageAsync(context, StatusCodes.Status409Conflict, result.Message).ConfigureAwait(false);
                return;
            case IntegrationAcceptOutcome.SessionClosed:
                await WriteMessageAsync(context, StatusCodes.Status409Conflict, result.Message, SessionClosedCode).ConfigureAwait(false);
                return;
            case IntegrationAcceptOutcome.SessionBusy:
                await WriteMessageAsync(context, StatusCodes.Status409Conflict, result.Message, SessionBusyCode).ConfigureAwait(false);
                return;
            case IntegrationAcceptOutcome.InputsRejected:
                await WriteMessageAsync(context, StatusCodes.Status422UnprocessableEntity, result.Message).ConfigureAwait(false);
                return;
            case IntegrationAcceptOutcome.SessionPolicyRejected:
                await WriteMessageAsync(context,
                        StatusCodes.Status422UnprocessableEntity,
                        result.Message,
                        IntegrationFailureCategories.SessionPolicy)
                    .ConfigureAwait(false);
                return;
            case IntegrationAcceptOutcome.QueueFull:
                context.Response.Headers.RetryAfter = "5";
                await WriteMessageAsync(context, StatusCodes.Status503ServiceUnavailable, result.Message).ConfigureAwait(false);
                return;
            default:
                // The credential was revoked between authentication and admission. It must not be distinguishable from
                // one that was already revoked when the request arrived, so this is the challenge's own answer.
                await WriteUnauthorizedAsync(context).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    ///     Reads at most <paramref name="cap" /> + 1 bytes and returns <see langword="null" /> the moment the total
    ///     passes the cap: a caller that lies about — or omits — <c>Content-Length</c> must not get to allocate more
    ///     than one byte past the limit.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpContext context, long cap)
    {
        var reader = context.Request.BodyReader;
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false);
            var sequence = result.Buffer;
            foreach (var segment in sequence)
            {
                var room = (int)Math.Min(segment.Length, (cap + 1) - writer.WrittenCount);
                if (room > 0)
                {
                    writer.Write(segment.Span[..room]);
                }
            }

            reader.AdvanceTo(sequence.End);

            if (result.IsCanceled)
            {
                // A cancelled read yields no further bytes, so looping on it spins the CPU until RequestAborted throws
                // on its own. Answer with the cancellation the reader already reported.
                throw new OperationCanceledException("The integration request body read was cancelled.", context.RequestAborted);
            }

            if (writer.WrittenCount > cap)
            {
                return null;
            }

            if (result.IsCompleted)
            {
                return writer.WrittenSpan.ToArray();
            }
        }
    }

    private static bool TryMapInputs(IReadOnlyList<IntegrationInvokeInput>? wire, out IReadOnlyList<IntegrationInputDto> inputs)
    {
        inputs = [];
        if (wire is null)
        {
            return false;
        }

        var mapped = new List<IntegrationInputDto>(wire.Count);
        foreach (var input in wire)
        {
            if (string.Equals(input.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                mapped.Add(new IntegrationInputDto(IntegrationInputKinds.Text, input.Text, input.Label, Json: null));
            }
            else if (string.Equals(input.Type, "json", StringComparison.OrdinalIgnoreCase))
            {
                // The RAW text the caller sent, never a re-serialisation: the seed must carry what was received.
                mapped.Add(new IntegrationInputDto(IntegrationInputKinds.Json, Text: null, input.Label, input.Json?.GetRawText()));
            }
            else
            {
                return false;
            }
        }

        inputs = mapped;
        return true;
    }

    private static bool TryReadExecutionId(HttpContext context, out Guid executionId) =>
        TryReadGuidRoute(context, "executionId", out executionId);

    private static bool TryReadGuidRoute(HttpContext context, string name, out Guid value)
    {
        value = Guid.Empty;
        return context.Request.RouteValues.TryGetValue(name, out var raw) && Guid.TryParse(raw as string, out value);
    }

    private static IntegrationExecutionLinks Links(Guid executionId) =>
        new(SelfPath(executionId), EventsPath(executionId));

    private static string SelfPath(Guid executionId) =>
        Path(LocalApiRoutes.IntegrationApi.ExecutionById, executionId);

    private static string EventsPath(Guid executionId) =>
        Path(LocalApiRoutes.IntegrationApi.ExecutionEvents, executionId);

    private static string Path(string route, Guid executionId) =>
        $"/{LocalApiRoutes.Prefix}/{route.Replace("{executionId}", executionId.ToString("D"), StringComparison.Ordinal)}";

    /// <summary>
    ///     The wire spelling of a status. An explicit map rather than a lower-cased <c>ToString</c>: the strings are the
    ///     external contract every caller branches on, so a renamed enum member must break the build here rather than
    ///     silently change what an integrator reads.
    /// </summary>
    private static string StatusName(IntegrationExecutionStatus status) =>
        status switch
        {
            IntegrationExecutionStatus.Accepted => "accepted",
            IntegrationExecutionStatus.Queued => "queued",
            IntegrationExecutionStatus.Running => "running",
            IntegrationExecutionStatus.Completed => "completed",
            IntegrationExecutionStatus.Failed => "failed",
            IntegrationExecutionStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown integration execution status.")
        };

    private static Task WriteUnauthorizedAsync(HttpContext context)
    {
        // Byte-identical to the authentication handler's challenge: same status, same header, no body. A distinguishable
        // answer would tell a caller its key was real.
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = IntegrationApiKeyAuthenticationHandler.BearerChallenge;
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Content-free by construction: never an echo of the caller's inputs. <paramref name="code" /> is the optional
    ///     machine-readable discriminator, present only on the refusals a caller can branch on — never on a masked 404.
    /// </summary>
    private static Task WriteMessageAsync(HttpContext context, int statusCode, string message, string? code = null)
    {
        context.Response.StatusCode = statusCode;
        return code is null
            ? context.Response.WriteAsJsonAsync(new
            {
                message
            }, context.RequestAborted)
            : context.Response.WriteAsJsonAsync(new
            {
                message,
                code
            }, context.RequestAborted);
    }
}

/// <summary>Endpoint metadata carrying the integration family's request-body cap, resolved once at composition.</summary>
internal sealed class IntegrationRequestSizeLimit(long maxRequestBodySize) : IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize { get; } = maxRequestBodySize;
}
