namespace XE_Local_AI_Engine.Tests.Integrations;

using System.IO.Pipelines;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one body-limit mechanism that is provable without a real Kestrel connection: the bounded read.
///     <para>
///         The other two — the <c>IRequestSizeLimitMetadata</c> the route carries and the
///         <see cref="IHttpMaxRequestBodySizeFeature" /> the handler sets — are Kestrel-side, and the in-memory test
///         host presents no Kestrel connection, so an assertion here would neither pass nor disprove anything about
///         them. They are proven in the live round. What IS asserted here is that a caller who lies about, or omits,
///         <c>Content-Length</c> gets the same 413 and never allocates more than one byte past the cap.
///     </para>
///     <para>
///         Mechanism 2's HANDLING is provable here even though its trigger is not: the throw Kestrel raises as it
///         consumes an oversized body is a plain <see cref="BadHttpRequestException" />, which a fake body pipe can
///         raise verbatim.
///     </para>
/// </summary>
public sealed class IntegrationApiHandlerBodyLimitTests
{
    private const int Cap = 512;

    [Test]
    public async Task Invoke_WhenContentLengthExceedsTheCap_Returns413FromTheEarlyExit()
    {
        using var fixture = new Fixture();
        var context = fixture.Context(new string('a', Cap * 4), truthfulContentLength: true);

        await fixture.Handler.InvokeAsync(context);

        AssertEx.Equal((int)HttpStatusCode.RequestEntityTooLarge, context.Response.StatusCode);
    }

    [Test]
    public async Task Invoke_WhenTheBodyExceedsTheCapWithNoContentLength_Returns413FromTheBoundedRead()
    {
        // The chunked shape. Content-Length is never trusted alone, so the bounded read is what has to catch this one.
        using var fixture = new Fixture();
        var context = fixture.Context(new string('a', Cap * 4), truthfulContentLength: false);

        await fixture.Handler.InvokeAsync(context);

        AssertEx.Equal((int)HttpStatusCode.RequestEntityTooLarge, context.Response.StatusCode);
        await fixture.Invocations.DidNotReceive().AcceptAsync(Arg.Any<IntegrationAcceptRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Invoke_WhenTheHostRefusesTheBodyMidRead_Returns413FromMechanismTwosCatch()
    {
        // What Kestrel does once IHttpMaxRequestBodySizeFeature is exceeded on a chunked body: the read itself throws.
        // The trigger needs a real connection, the handling does not.
        using var fixture = new Fixture();
        var context = fixture.ContextFor("{}"u8.ToArray(), truthfulContentLength: false);
        context.Features.Set<IRequestBodyPipeFeature>(new StubBodyPipeFeature(new ThrowingPipeReader()));

        await fixture.Handler.InvokeAsync(context);

        AssertEx.Equal((int)HttpStatusCode.RequestEntityTooLarge, context.Response.StatusCode, "A host-refused body is oversized, not malformed.");
        await fixture.Invocations.DidNotReceive().AcceptAsync(Arg.Any<IntegrationAcceptRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Invoke_WhenTheBodyReadIsCancelled_StopsInsteadOfSpinningOnTheCancelledResult()
    {
        // A ReadResult with IsCanceled set yields no further bytes. Ignoring it burned the loop until RequestAborted
        // happened to throw on its own.
        using var fixture = new Fixture();
        var context = fixture.ContextFor("{}"u8.ToArray(), truthfulContentLength: false);
        var reader = new CancellingPipeReader();
        context.Features.Set<IRequestBodyPipeFeature>(new StubBodyPipeFeature(reader));

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => fixture.Handler.InvokeAsync(context));
        AssertEx.True(reader.ReadCount <= CancellingPipeReader.SpinCeiling, "The cancelled result must end the loop, not be read past.");
    }

    [Test]
    public async Task Invoke_WhenTheBodyIsUnderTheCapButNotJson_Returns400NotA413()
    {
        using var fixture = new Fixture();
        var context = fixture.ContextFor("this is not json"u8.ToArray(), truthfulContentLength: true);

        await fixture.Handler.InvokeAsync(context);

        AssertEx.Equal((int)HttpStatusCode.BadRequest, context.Response.StatusCode, "Malformed is a distinct failure from oversized, and gets a distinct code.");
    }

    [Test]
    public async Task Invoke_WhenTheRouteCarriesNoSizeMetadata_ThrowsRatherThanGuessing()
    {
        using var fixture = new Fixture();
        var context = fixture.ContextFor("{}"u8.ToArray(), truthfulContentLength: true, withMetadata: false);

        // A missing metadata entry is a wiring bug in Program.cs, not a runtime condition to paper over with a default
        // that would silently be the wrong number.
        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => fixture.Handler.InvokeAsync(context));
    }

    [Test]
    public async Task Invoke_WhenTheCapMoves_TheBoundaryMovesWithTheMetadata()
    {
        using var fixture = new Fixture();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            requestId = Guid.NewGuid(),
            inputs = new[]
            {
                new
                {
                    type = "text",
                    text = new string('a', 200)
                }
            }
        }));

        var tight = fixture.ContextFor(body, truthfulContentLength: false, cap: body.Length - 1);
        await fixture.Handler.InvokeAsync(tight);
        AssertEx.Equal((int)HttpStatusCode.RequestEntityTooLarge, tight.Response.StatusCode, "All three layers read the cap from the route's own metadata.");

        var roomy = fixture.ContextFor(body, truthfulContentLength: false, cap: body.Length + 1);
        await fixture.Handler.InvokeAsync(roomy);
        AssertEx.NotEqual((int)HttpStatusCode.RequestEntityTooLarge, roomy.Response.StatusCode, "The same body under a wider cap must reach the accept path.");
    }

    [Test]
    public async Task Invoke_WhenTheCallerHasNoIdentityClaim_Returns401WithTheChallengeAndReadsNoBody()
    {
        using var fixture = new Fixture();
        var context = fixture.ContextFor("{}"u8.ToArray(), truthfulContentLength: true, authenticated: false);

        await fixture.Handler.InvokeAsync(context);

        AssertEx.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
        AssertEx.Equal(expected: 0L, context.Response.Body.Length, "The challenge writes no body, and a mid-request revocation must be byte-identical to it.");
    }

    [Test]
    public async Task Invoke_WhenThePrincipalBudgetIsSpent_Returns429BeforeTheBodyIsRead()
    {
        using var fixture = new Fixture(permitLimit: 1);
        await fixture.Handler.InvokeAsync(fixture.ContextFor("{}"u8.ToArray(), truthfulContentLength: true));

        // An oversized body from a principal already over its budget must cost one dictionary lookup, not a megabyte of
        // buffered reads — which is why the limiter runs before the body is touched.
        var context = fixture.Context(new string('a', Cap * 4), truthfulContentLength: true);
        await fixture.Handler.InvokeAsync(context);

        AssertEx.Equal((int)HttpStatusCode.TooManyRequests, context.Response.StatusCode);
        AssertEx.Equal("60",
            context.Response.Headers.RetryAfter.ToString(),
            "Retry-After matches the window and the middleware's own rejection, so a caller sees one convention whichever layer refused it.");
    }

    private sealed class StubBodyPipeFeature(PipeReader reader) : IRequestBodyPipeFeature
    {
        public PipeReader Reader { get; } = reader;
    }

    /// <summary>Raises exactly what Kestrel raises when the request body passes the host cap as it is consumed.</summary>
    private sealed class ThrowingPipeReader : PipeReader
    {
        public override void AdvanceTo(SequencePosition consumed)
        {
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
        }

        public override void CancelPendingRead()
        {
        }

        public override void Complete(Exception? exception = null)
        {
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        public override bool TryRead(out ReadResult result)
        {
            result = default;
            return false;
        }
    }

    /// <summary>Answers cancelled reads, bounded so a handler that ignores the flag fails the assertion rather than hanging.</summary>
    private sealed class CancellingPipeReader : PipeReader
    {
        public const int SpinCeiling = 8;

        public int ReadCount { get; private set; }

        public override void AdvanceTo(SequencePosition consumed)
        {
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
        }

        public override void CancelPendingRead()
        {
        }

        public override void Complete(Exception? exception = null)
        {
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ReadCount > SpinCeiling
                ? ValueTask.FromResult(new ReadResult(default, isCanceled: false, isCompleted: true))
                : ValueTask.FromResult(new ReadResult(default, isCanceled: true, isCompleted: false));
        }

        public override bool TryRead(out ReadResult result)
        {
            result = default;
            return false;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Guid _principalId = Guid.NewGuid();
        private readonly IntegrationPrincipalRateLimiter _rateLimiter;
        private readonly IntegrationSseWriter _writer;

        public Fixture(int permitLimit = 100)
        {
            _rateLimiter = new IntegrationPrincipalRateLimiter(permitLimit);
            _writer = new IntegrationSseWriter(Substitute.For<IIntegrationExecutionEventBuffer>(),
                Options.Create(new IntegrationOptions()),
                TimeProvider.System,
                NullLogger<IntegrationSseWriter>.Instance);
            Invocations.AcceptAsync(Arg.Any<IntegrationAcceptRequest>(), Arg.Any<CancellationToken>())
                       .Returns(new IntegrationAcceptResult(IntegrationAcceptOutcome.TriggerNotFound, ExecutionId: null, SessionId: null, Status: null, "No such trigger."));

            var executions = new FakeIntegrationExecutionStore();
            var triggers = new FakeIntegrationTriggerStore();
            var sessions = new FakeIntegrationSessionStore();
            var keys = new FakeIntegrationApiKeyStore();
            Handler = new IntegrationApiHandler(Invocations,
                new IntegrationExternalAccess(executions, sessions, keys),
                new IntegrationExecutionQueryService(executions,
                    triggers,
                    Substitute.For<IIntegrationExecutionEventBuffer>(),
                    new IntegrationCancellationRegistry(),
                    TimeProvider.System,
                    NullLogger<IntegrationExecutionQueryService>.Instance),
                new IntegrationSessionService(sessions,
                    executions,
                    triggers,
                    new IntegrationExternalAccess(executions, sessions, keys),
                    Substitute.For<INodeChatPersistenceService>(),
                    new IntegrationSessionGate(),
                    TimeProvider.System,
                    NullLogger<IntegrationSessionService>.Instance),
                _writer,
                _rateLimiter);
        }

        public void Dispose()
        {
            _rateLimiter.Dispose();
            _writer.Dispose();
        }

        public IntegrationApiHandler Handler { get; }

        public IIntegrationInvocationService Invocations { get; } = Substitute.For<IIntegrationInvocationService>();

        public DefaultHttpContext Context(string filler, bool truthfulContentLength) =>
            ContextFor(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                requestId = Guid.NewGuid(),
                inputs = new[]
                {
                    new
                    {
                        type = "text",
                        text = filler
                    }
                }
            })), truthfulContentLength);

        public DefaultHttpContext ContextFor(byte[] body,
            bool truthfulContentLength,
            bool withMetadata = true,
            long cap = Cap,
            bool authenticated = true)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/local/v1/integration-api/triggers/sensor-ingest/invoke";
            context.Request.RouteValues["triggerName"] = "sensor-ingest";
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentLength = truthfulContentLength ? body.Length : null;
            context.Response.Body = new MemoryStream();

            if (authenticated)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(NodeAuthorizationPolicies.IntegrationPrincipalClaimType, _principalId.ToString("D")),
                    new Claim(NodeAuthorizationPolicies.IntegrationKeyPrefixClaimType, "xeint_abcdefgh")
                ], "IntegrationApiKey"));
            }

            if (withMetadata)
            {
                context.SetEndpoint(new Endpoint(requestDelegate: null,
                    new EndpointMetadataCollection(new IntegrationRequestSizeLimit(cap)),
                    "integration-invoke"));
            }

            return context;
        }
    }
}
