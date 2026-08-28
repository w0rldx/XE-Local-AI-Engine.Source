namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompat;

using System.Net;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The outbound guard is what makes the operator's reviewed base URL the ONLY destination a connection can reach.
///     Its locality declaration — which unlocks workspace tools, the knowledge base and <c>run_python</c> — is granted
///     against that one address, so anything that could move the destination has to be refused rather than trusted.
/// </summary>
public sealed class ExternalEndpointGuardHandlerTests
{
    private static readonly Uri PinnedBase = new("https://api.example.com:8443/v1/");

    [Test]
    [Arguments("https://api.example.com:8443/v1/chat/completions", true)]
    [Arguments("https://api.example.com:8443/v1/models", true)]
    [Arguments("https://api.example.com:8443/v1/", true)]
    // Scheme downgrade, different host, different port: each is a different service, not a deeper path.
    [Arguments("http://api.example.com:8443/v1/chat/completions", false)]
    [Arguments("https://evil.example.com:8443/v1/chat/completions", false)]
    [Arguments("https://api.example.com:9443/v1/chat/completions", false)]
    // A sibling prefix must not pass as a child of "/v1/" — the pinned base's trailing slash is what forces that.
    [Arguments("https://api.example.com:8443/v1x/chat", false)]
    [Arguments("https://api.example.com:8443/admin", false)]
    // Credentials smuggled into the target are refused even when the origin matches.
    [Arguments("https://user:pw@api.example.com:8443/v1/chat", false)]
    public void IsWithinPinnedEndpoint_AdmitsOnlyDescendantsOfTheReviewedBase(string target, bool expected)
    {
#pragma warning disable CA2000 // The inner handler transfers to the guard, which this scope disposes.
        using var guard = new ExternalEndpointGuardHandler(PinnedBase, new HttpClientHandler());
#pragma warning restore CA2000

        AssertEx.Equal(expected, guard.IsWithinPinnedEndpoint(new Uri(target, UriKind.Absolute)));
    }

    [Test]
    public async Task SendAsync_ToAnOffBaseTarget_IsRefusedBeforeItReachesTheTransport()
    {
#pragma warning disable CA2000 // The probe transfers to the guard, which transfers to the HttpClient this scope disposes.
        var probe = new CountingHandler();
        using var client = new HttpClient(new ExternalEndpointGuardHandler(PinnedBase, probe), disposeHandler: true);
#pragma warning restore CA2000

        _ = await AssertEx.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("https://evil.example.com/v1/models")));

        AssertEx.Equal(0, probe.CallCount);
    }

    [Test]
    public async Task SendAsync_ToAnOnBaseTarget_ReachesTheTransport()
    {
#pragma warning disable CA2000 // The probe transfers to the guard, which transfers to the HttpClient this scope disposes.
        var probe = new CountingHandler();
        using var client = new HttpClient(new ExternalEndpointGuardHandler(PinnedBase, probe), disposeHandler: true);
#pragma warning restore CA2000

        using var response = await client.GetAsync(new Uri("https://api.example.com:8443/v1/models"));

        AssertEx.Equal(1, probe.CallCount);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Test]
    public void Construction_WithARelativeBase_Throws()
    {
#pragma warning disable CA2000 // The construction throws, so nothing is left to dispose.
        _ = AssertEx.Throws<ArgumentException>(() => new ExternalEndpointGuardHandler(new Uri("/v1/", UriKind.Relative), new HttpClientHandler()).Dispose());
#pragma warning restore CA2000
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
