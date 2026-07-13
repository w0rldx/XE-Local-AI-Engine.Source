namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the <see cref="CodexAuthHandler" /> 401-retry path: a 401 triggers a single-flight
///     refresh and exactly one retry, and — because a sent <see cref="HttpRequestMessage" /> cannot be resent — the
///     retry goes on a fresh CLONE of the request carrying the original (buffered) content and the refreshed bearer.
/// </summary>
public sealed class CodexAuthHandlerTests
{
    [Test]
    public async Task SendAsync_On401_RefreshesAndRetriesOnceWithAClonedRequestAndRefreshedToken()
    {
        var tokenStore = Substitute.For<ICodexTokenStore>();
        var initial = new CodexTokens("initial-access", "refresh", DateTimeOffset.UtcNow.AddMinutes(30), "acct");
        var refreshed = new CodexTokens("refreshed-access", "refresh2", DateTimeOffset.UtcNow.AddMinutes(30), "acct");
        tokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(initial);

        var authService = Substitute.For<ICodexAuthService>();
        authService.RefreshAsync(Arg.Any<CodexTokens>(), Arg.Any<CancellationToken>()).Returns(refreshed);

        var inner = new SequencedInnerHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var handler = new CodexAuthHandler(Options.Create(new CodexOptions()),
            tokenStore,
            authService,
            NullLogger<CodexAuthHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/codex/responses")
        {
            Content = new StringContent(content: """{"model":"gpt-5-codex"}""", Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);

        // The retry succeeded (200) — proving the cloned request was re-sendable.
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 2, inner.Requests.Count);

        // Retry went on a DIFFERENT request instance (a clone), not the already-sent original.
        AssertEx.False(ReferenceEquals(inner.Requests[0], inner.Requests[1]), "retry must use a cloned request");

        // The clone preserved the original body and carried the refreshed bearer token.
        AssertEx.Equal(expected: """{"model":"gpt-5-codex"}""", inner.Bodies[1]);
        AssertEx.Equal("refreshed-access", inner.Requests[1].Headers.Authorization?.Parameter);

        await authService.Received(1).RefreshAsync(Arg.Any<CodexTokens>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAsync_WhenFirstAttemptSucceeds_DoesNotRefreshOrRetry()
    {
        var tokenStore = Substitute.For<ICodexTokenStore>();
        tokenStore.LoadAsync(Arg.Any<CancellationToken>())
                  .Returns(new CodexTokens("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(30), "acct"));
        var authService = Substitute.For<ICodexAuthService>();

        var inner = new SequencedInnerHandler(HttpStatusCode.OK);
        using var handler = new CodexAuthHandler(Options.Create(new CodexOptions()),
            tokenStore,
            authService,
            NullLogger<CodexAuthHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/codex/responses");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(expected: 1, inner.Requests.Count);
        await authService.DidNotReceive().RefreshAsync(Arg.Any<CodexTokens>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAsync_ByDefault_SendsHonestProductOriginatorAndUserAgent_NotCodexCli()
    {
        // The handler sends honest product identifiers rather than impersonating the official Codex CLI (ToS).
        var inner = await SendOnceWithOptions(new CodexOptions());

        var originator = inner.Requests[0].Headers.GetValues("originator").Single();
        var userAgent = inner.Requests[0].Headers.GetValues("User-Agent").Single();

        AssertEx.Equal("xe-local-ai-engine", originator);
        AssertEx.True(userAgent.StartsWith("XE-Local-AI-Engine/", StringComparison.Ordinal), $"unexpected UA: {userAgent}");
        AssertEx.False(originator.Contains("codex_cli", StringComparison.OrdinalIgnoreCase), "must not impersonate the Codex CLI");
        AssertEx.False(userAgent.Contains("codex_cli", StringComparison.OrdinalIgnoreCase), "must not impersonate the Codex CLI");
    }

    [Test]
    public async Task SendAsync_SetsAFreshSessionIdHeader_PerRequest()
    {
        // LIVE-CORRECTNESS: the Responses path carries a per-request session-id GUID (opencode reference).
        var inner = await SendOnceWithOptions(new CodexOptions());

        var sessionId = inner.Requests[0].Headers.GetValues("session-id").Single();
        AssertEx.True(Guid.TryParse(sessionId, out _), $"session-id must be a GUID, got: {sessionId}");
    }

    [Test]
    public async Task SendAsync_WhenOriginatorAndUserAgentOverridden_SendsTheConfiguredValues()
    {
        // Override escape hatch: if the subscription endpoint requires a Codex-compatible identifier, the operator can
        // override both via the CodexOAuth config section without a recompile.
        var inner = await SendOnceWithOptions(new CodexOptions
        {
            Originator = "codex_cli_rs",
            UserAgent = "codex_cli_rs"
        });

        AssertEx.Equal("codex_cli_rs", inner.Requests[0].Headers.GetValues("originator").Single());
        AssertEx.Equal("codex_cli_rs", inner.Requests[0].Headers.GetValues("User-Agent").Single());
    }

    [Test]
    public async Task SendAsync_OnFailure_LogsBoundedSanitizedBody_AndLeavesContentReadableForTheSdk()
    {
        // A multiline body must be logged as ONE sanitized line (no CR/LF), and the buffered content must still be
        // fully re-readable by the OpenAI SDK afterwards (the diagnostic read rewinds the stream).
        const string body = "first-line\r\nsecond-line\ninjected-forged-log-entry";
        var logger = new CapturingLogger();

        using var response = await SendFailureAndCapture(HttpStatusCode.BadRequest, body, logger);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = AssertSingleWarning(logger);
        AssertEx.False(message.Contains('\n', StringComparison.Ordinal), "logged message must not contain a newline (log-injection guard)");
        AssertEx.False(message.Contains('\r', StringComparison.Ordinal), "logged message must not contain a carriage return (log-injection guard)");
        AssertEx.True(message.Contains("first-line", StringComparison.Ordinal), "the excerpt should still carry the body text");
        AssertEx.True(message.Contains("truncated=False", StringComparison.Ordinal), $"unexpected message: {message}");

        // The SDK still reads the full, unmodified body from the buffered content.
        var readBack = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(body, readBack);
    }

    [Test]
    public async Task SendAsync_OnFailure_TruncatesAnOversizedBody_ToTheLoggedByteCap()
    {
        // A hostile/large body must be truncated: the total length is reported but the logged excerpt is capped.
        const int cap = 2048;
        var body = new string('a', 5000);
        var logger = new CapturingLogger();

        using var response = await SendFailureAndCapture(HttpStatusCode.BadGateway, body, logger);

        AssertEx.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var message = AssertSingleWarning(logger);
        AssertEx.True(message.Contains("5000 bytes", StringComparison.Ordinal), $"total length not reported: {message}");
        AssertEx.True(message.Contains("truncated=True", StringComparison.Ordinal), $"truncation not reported: {message}");
        // The whole (5000-char) body must not appear; the excerpt is capped, so the rendered line stays near the cap.
        AssertEx.True(message.Length < body.Length, "the full oversized body must not be logged");
        AssertEx.True(message.Length <= cap + 300, $"the logged excerpt exceeded the byte cap: length {message.Length}");
    }

    [Test]
    public async Task SendAsync_OnFailure_DoesNotLogATokenLikeStringBeyondTheBound()
    {
        // A token-like secret placed entirely beyond the byte cap must never reach the log.
        const string tokenLike = "SECRET-TOKEN-0123456789abcdef";
        var body = new string('x', 2048) + tokenLike;
        var logger = new CapturingLogger();

        using var response = await SendFailureAndCapture(HttpStatusCode.BadRequest, body, logger);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = AssertSingleWarning(logger);
        AssertEx.False(message.Contains(tokenLike, StringComparison.Ordinal), "content beyond the byte cap must not be logged");
        AssertEx.True(message.Contains("truncated=True", StringComparison.Ordinal), $"truncation not reported: {message}");
    }

    // Drives one non-401 failure response carrying the given body through the handler with a capturing logger. A 4xx/5xx
    // that is NOT 401 exercises the diagnostic-logging path directly without the refresh/retry branch.
    private static async Task<HttpResponseMessage> SendFailureAndCapture(HttpStatusCode status, string body, ILogger<CodexAuthHandler> logger)
    {
        var tokenStore = Substitute.For<ICodexTokenStore>();
        tokenStore.LoadAsync(Arg.Any<CancellationToken>())
                  .Returns(new CodexTokens("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(30), "acct"));
        var authService = Substitute.For<ICodexAuthService>();

        var inner = new SingleBodyResponseHandler(status, body);
        using var handler = new CodexAuthHandler(Options.Create(new CodexOptions()),
            tokenStore,
            authService,
            logger)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/codex/responses");
        return await client.SendAsync(request);
    }

    private static string AssertSingleWarning(CapturingLogger logger)
    {
        AssertEx.ContainsSingle(logger.Entries, entry => entry.Level == LogLevel.Warning);
        return logger.Entries.Single(entry => entry.Level == LogLevel.Warning).Message;
    }

    private static async Task<SequencedInnerHandler> SendOnceWithOptions(CodexOptions options)
    {
        var tokenStore = Substitute.For<ICodexTokenStore>();
        tokenStore.LoadAsync(Arg.Any<CancellationToken>())
                  .Returns(new CodexTokens("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(30), "acct"));
        var authService = Substitute.For<ICodexAuthService>();

        var inner = new SequencedInnerHandler(HttpStatusCode.OK);
        using var handler = new CodexAuthHandler(Options.Create(options),
            tokenStore,
            authService,
            NullLogger<CodexAuthHandler>.Instance)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/codex/responses");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return inner;
    }

    /// <summary>An inner handler that returns a single response of the given status carrying a fixed body.</summary>
    private sealed class SingleBodyResponseHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public SingleBodyResponseHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Captures every logged entry (level + rendered message) so the diagnostic log path can be asserted.</summary>
    private sealed class CapturingLogger : ILogger<CodexAuthHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>An inner handler that returns a queued status per call and records each request + its body.</summary>
    private sealed class SequencedInnerHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses;

        public SequencedInnerHandler(params HttpStatusCode[] statuses)
        {
            _statuses = new Queue<HttpStatusCode>(statuses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.InternalServerError;
            return new HttpResponseMessage(status);
        }
    }
}
