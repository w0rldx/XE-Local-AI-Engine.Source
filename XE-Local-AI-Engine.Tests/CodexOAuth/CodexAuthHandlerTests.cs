namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
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
