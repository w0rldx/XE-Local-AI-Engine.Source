namespace XE_Local_AI_Engine.Tests.Endpoints.ExternalProviders;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The probe against a stubbed transport: what it sends, and how each shape of answer becomes a verdict.
/// </summary>
/// <remarks>
///     The verdict policy is the substance here. "Reachable" means the endpoint ANSWERED — a gateway that serves only
///     <c>POST /v1/chat/completions</c> and 404s the listing is a perfectly usable connection, so reporting it as
///     unreachable would make the probe refuse working setups. Only a transport failure is unreachable.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ExternalProviderProbeServiceTests
{
    private const string StoredKey = "sk-unsloth-super-secret";
    private const string StoredHeaderValue = "stored-header-super-secret";

    [Test]
    public async Task Probe_WhenTheListingIsWellFormed_ReturnsTheIdsAndTheDeclaredWindows()
    {
        var transport = new ProbeTransport(_ => Json("""
                                                     {"object":"list","data":[
                                                       {"id":"qwen3-27b","max_model_len":32768},
                                                       {"id":"org/other-model","context_length":8192},
                                                       {"id":"no-window"}
                                                     ]}
                                                     """));
        var service = CreateService(transport);

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://127.0.0.1:18099", ApiKey: null));

        AssertEx.Equal(ExternalProviderProbeOutcome.Answered, result.Outcome);
        AssertEx.Null(result.Error);
        AssertEx.Equal(expected: 3, result.Models.Count);
        AssertEx.Equal("qwen3-27b", result.Models[0].Id);
        AssertEx.Equal(expected: 32768, result.Models[0].ContextLength);
        AssertEx.Equal(expected: 8192, result.Models[1].ContextLength);
        AssertEx.Null(result.Models[2].ContextLength);

        // Normalized exactly once, by the same normalizer the save path and the outbound chat guard use — a probe that
        // spelled the address differently would be validating something the transport never sends to.
        AssertEx.Equal("http://127.0.0.1:18099/v1/models", transport.LastRequestUri?.ToString());
    }

    [Test]
    public async Task Probe_DropsIdsThatCouldNeverBeRegistered()
    {
        // A pick-to-add row whose id fails the wire-id grammar would produce a registration the store always rejects.
        var transport = new ProbeTransport(_ => Json("""
                                                     {"object":"list","data":[{"id":"good-model"},{"id":"../escape"},{"id":""},{"id":"good-model"}]}
                                                     """));
        var service = CreateService(transport);

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://127.0.0.1:18099", ApiKey: null));

        AssertEx.Equal("good-model", result.Models.Single().Id);
    }

    [Test]
    public async Task Probe_WhenTheEndpointServesNoListing_IsStillReachableWithAnExplanation()
    {
        var transport = new ProbeTransport(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = CreateService(transport);

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://127.0.0.1:18099", ApiKey: null));

        AssertEx.Equal(ExternalProviderProbeOutcome.Answered, result.Outcome);
        AssertEx.Empty(result.Models);
        AssertEx.NotNull(result.Error);
    }

    [Test]
    public async Task Probe_WhenTheBodyIsNotAModelListing_IsStillReachableWithAnExplanation()
    {
        var transport = new ProbeTransport(_ => Json("<html>not json</html>"));
        var service = CreateService(transport);

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://127.0.0.1:18099", ApiKey: null));

        AssertEx.Equal(ExternalProviderProbeOutcome.Answered, result.Outcome);
        AssertEx.Empty(result.Models);
        AssertEx.NotNull(result.Error);
    }

    [Test]
    public async Task Probe_WhenTheEndpointRedirects_ReportsTheRedirectRatherThanFollowingIt()
    {
        // Following it would move the probe to a host the operator never reviewed and then report THAT host as the
        // connection's reachability.
        var transport = new ProbeTransport(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://elsewhere.example/v1/models");
            return response;
        });
        var service = CreateService(transport);

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://127.0.0.1:18099", ApiKey: null));

        AssertEx.Equal(ExternalProviderProbeOutcome.Answered, result.Outcome);
        AssertEx.Empty(result.Models);
        AssertEx.True(AssertEx.NotNull(result.Error).Contains("redirect", StringComparison.OrdinalIgnoreCase));
        AssertEx.Equal(expected: 1, transport.RequestCount);
    }

    [Test]
    public async Task Probe_WhenTheTransportFails_ReportsUnreachableWithoutLeakingTheExceptionText()
    {
        var transport = new ProbeTransport(_ => throw new HttpRequestException("connection refused to http://127.0.0.1:18099"));
        var service = CreateService(transport);

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://127.0.0.1:18099", ApiKey: null));

        AssertEx.Equal(ExternalProviderProbeOutcome.Unreachable, result.Outcome);
        AssertEx.False(AssertEx.NotNull(result.Error).Contains("connection refused", StringComparison.Ordinal));
    }

    [Test]
    public async Task Probe_WhenTheBaseUrlIsUnusable_RefusesBeforeSendingAnything()
    {
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport);

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "ftp://box/v1", ApiKey: null));

        AssertEx.Equal(ExternalProviderProbeOutcome.InvalidBaseUrl, result.Outcome);
        AssertEx.Equal(expected: 0, transport.RequestCount);
    }

    [Test]
    public async Task Probe_ForAPlainHttpRemoteAddressWithoutTheOptIn_RefusesBeforeSendingAnything()
    {
        // "Test connection" must not send the key over the channel the save would refuse.
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://192.168.1.20:8000/v1", "sk-lan"));

        AssertEx.Equal(ExternalProviderProbeOutcome.InvalidBaseUrl, result.Outcome);
        AssertEx.Equal(ExternalProviderTransportPolicy.InsecureRemoteError, result.Error);
        AssertEx.Equal(expected: 0, transport.RequestCount);

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://192.168.1.20:8000/v1", "sk-lan", AllowInsecureHttp: true));

        AssertEx.Equal(expected: 1, transport.RequestCount);
    }

    [Test]
    public async Task Probe_ForAStoredInsecureRemoteConnection_IgnoresTheStoredOptIn_AndHonoursOnlyTheRequests()
    {
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, new StoredExternalProviderConfig
        {
            Revision = "rev-1",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "unsloth-box",
                    DisplayName = "Unsloth box",
                    BaseUrl = "http://192.168.1.20:8000/v1/",
                    ApiKey = StoredKey,
                    Locality = ExternalProviderLocality.Local,
                    AllowInsecureHttp = true
                }
            ]
        });

        var withdrawn = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", BaseUrl: null, ApiKey: null));
        AssertEx.Equal(ExternalProviderTransportPolicy.InsecureRemoteError, withdrawn.Error, "An unticked editor sends no flag; the stored consent must not revive it.");
        AssertEx.Equal(expected: 0, transport.RequestCount, "Refused before any request carried the stored key over plain http.");

        var consented = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", BaseUrl: null, ApiKey: null, AllowInsecureHttp: true));
        AssertEx.Equal(ExternalProviderProbeOutcome.Answered, consented.Outcome, "The request's own opt-in is what counts.");
        AssertEx.Equal(expected: 1, transport.RequestCount);
    }

    [Test]
    public async Task Probe_WhenTheConnectionIdIsNotStored_RefusesBeforeSendingAnything()
    {
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery("not-configured", BaseUrl: null, ApiKey: null));

        AssertEx.Equal(ExternalProviderProbeOutcome.UnknownConnection, result.Outcome);
        AssertEx.Equal(expected: 0, transport.RequestCount);
    }

    [Test]
    public async Task Probe_ForAStoredConnection_UsesItsAddressAndItsStoredKey()
    {
        // The masked editor sends no key back, so "Test connection" on an existing connection only works if the stored
        // one is used — and it must be USED, never returned.
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        var result = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", BaseUrl: null, ApiKey: null));

        AssertEx.Equal(ExternalProviderProbeOutcome.Answered, result.Outcome);
        AssertEx.Equal("http://127.0.0.1:18099/v1/models", transport.LastRequestUri?.ToString());
        AssertEx.Equal($"Bearer {StoredKey}", transport.LastAuthorization);
    }

    [Test]
    public async Task Probe_ForADraftPathOnTheStoredOrigin_TestsTheDraftWithTheStoredKey()
    {
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        // Same scheme, host and port: the credential's audience has not moved, so editing only the path must not force
        // the operator to re-type a masked secret.
        _ = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", "http://127.0.0.1:18099/openai/v1", ApiKey: null));

        AssertEx.Equal("http://127.0.0.1:18099/openai/v1/models", transport.LastRequestUri?.ToString());
        AssertEx.Equal($"Bearer {StoredKey}", transport.LastAuthorization);
    }

    [Test]
    public async Task Probe_ForADraftAddressOnAnotherOrigin_DoesNotForwardTheStoredKey()
    {
        // THE exfiltration path. An Operator API caller who cannot read the encrypted key could otherwise point the
        // probe at a listener they control and have the node present the stored secret as a bearer token. Testing a
        // moved endpoint probes with only what the caller supplied — here, nothing.
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", "http://attacker.example.com/v1", ApiKey: null, AllowInsecureHttp: true));

        AssertEx.Equal("http://attacker.example.com/v1/models", transport.LastRequestUri?.ToString());
        AssertEx.False(transport.LastRequestHadAuthorization);
    }

    [Test]
    public async Task Probe_ForADraftAddressOnAnotherOrigin_UsesTheKeyTheCallerSupplied()
    {
        // The legitimate half of the same flow: an operator moving a connection and re-entering its key must be able to
        // test the new endpoint before saving.
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", "http://127.0.0.1:19000/v1", "sk-new-endpoint"));

        AssertEx.Equal("Bearer sk-new-endpoint", transport.LastAuthorization);
    }

    [Test]
    public async Task Probe_ForAKeylessDraft_SendsNoAuthorizationHeaderAtAll()
    {
        // An empty bearer is not the same as no bearer: a local llama-server rejects nothing, but an endpoint that DOES
        // check would fail an empty token outright.
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport);

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null, "http://127.0.0.1:18099", ApiKey: null));

        AssertEx.False(transport.LastRequestHadAuthorization);
    }

    [Test]
    public async Task Probe_ForADraft_SendsItsHeaderRows()
    {
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport);

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery(ConnectionId: null,
            "http://127.0.0.1:18099",
            "demo-jwt",
            Headers: [Header(" X-Demo-Project ", "demo"), Header("X-Demo-Token", "typed", isSecret: true), Header("", "")]));

        AssertEx.Equal("Bearer demo-jwt", transport.LastAuthorization);
        AssertEx.Equal("demo", transport.LastHeaders.GetValueOrDefault("X-Demo-Project"));
        AssertEx.Equal("typed", transport.LastHeaders.GetValueOrDefault("X-Demo-Token"));
    }

    [Test]
    [Arguments(null)]
    [Arguments("http://127.0.0.1:18099/openai/v1")]
    public async Task Probe_ForAStoredConnection_ABlankSecretHeaderRowUsesTheStoredValueOnItsOrigin(string? draftBaseUrl)
    {
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box",
            draftBaseUrl,
            ApiKey: null,
            Headers: [Header("x-stored-token", value: null, isSecret: true)]));

        AssertEx.Equal(StoredHeaderValue, transport.LastHeaders.GetValueOrDefault("X-Stored-Token"));
    }

    [Test]
    public async Task Probe_ForADraftAddressOnAnotherOrigin_DoesNotForwardAStoredSecretHeaderValue()
    {
        // The key's exfiltration path, for a gateway token kept in a header instead.
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box",
            "http://attacker.example.com/v1",
            ApiKey: null,
            AllowInsecureHttp: true,
            Headers: [Header("X-Stored-Token", value: null, isSecret: true), Header("X-Demo-Project", "demo")]));

        AssertEx.Equal(expected: 1, transport.RequestCount);
        AssertEx.False(transport.LastHeaders.ContainsKey("X-Stored-Token"));
        AssertEx.False(transport.LastHeaders.Values.Any(static value => value.Contains(StoredHeaderValue, StringComparison.Ordinal)));
        AssertEx.Equal("demo", transport.LastHeaders.GetValueOrDefault("X-Demo-Project"));
    }

    [Test]
    [Arguments(null)]
    [Arguments("http://127.0.0.1:18099/openai/v1")]
    public async Task Probe_ForAStoredConnectionWithNoHeaderRows_SendsTheStoredHeadersOnItsOrigin(string? draftBaseUrl)
    {
        // A caller that probes a saved connection without rows gets the stored headers, as a blank key gets the stored
        // key; otherwise a header-gated gateway answers 403 to every such probe.
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", draftBaseUrl, ApiKey: null, Headers: null));

        AssertEx.Equal(StoredHeaderValue, transport.LastHeaders.GetValueOrDefault("X-Stored-Token"));
    }

    [Test]
    public async Task Probe_ForAStoredConnectionWithNoHeaderRows_DoesNotSendTheStoredHeadersToAnotherOrigin()
    {
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", "http://attacker.example.com/v1", ApiKey: null, AllowInsecureHttp: true, Headers: null));

        AssertEx.Equal(expected: 1, transport.RequestCount);
        AssertEx.False(transport.LastHeaders.Values.Any(static value => value.Contains(StoredHeaderValue, StringComparison.Ordinal)));
    }

    [Test]
    public async Task Probe_ForAStoredConnectionWithAnEmptyRowList_SendsNoCustomHeaders()
    {
        // An explicit empty list is the editor saying "this draft has no headers", which must not be overridden.
        var transport = new ProbeTransport(_ => Json("{\"data\":[]}"));
        var service = CreateService(transport, CreateConfig());

        _ = await service.ProbeAsync(new ExternalProviderProbeQuery("unsloth-box", BaseUrl: null, ApiKey: null, Headers: []));

        AssertEx.False(transport.LastHeaders.ContainsKey("X-Stored-Token"));
    }

    private static StoredExternalProviderHeader Header(string name, string? value, bool isSecret = false)
    {
        return new StoredExternalProviderHeader
        {
            Name = name,
            Value = value,
            IsSecret = isSecret
        };
    }

    private static StoredExternalProviderConfig CreateConfig()
    {
        return new StoredExternalProviderConfig
        {
            Revision = "rev-1",
            Connections =
            [
                new StoredExternalProviderConnection
                {
                    Id = "unsloth-box",
                    DisplayName = "Unsloth box",
                    BaseUrl = "http://127.0.0.1:18099/v1/",
                    ApiKey = StoredKey,
                    Locality = ExternalProviderLocality.Local,
                    Headers = [Header("X-Stored-Token", StoredHeaderValue, isSecret: true)]
                }
            ]
        };
    }

    private static ExternalProviderProbeService CreateService(ProbeTransport transport, StoredExternalProviderConfig? config = null)
    {
        var store = Substitute.For<IExternalProviderStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(config ?? new StoredExternalProviderConfig());
        return new ExternalProviderProbeService(store, NullLogger<ExternalProviderProbeService>.Instance, transport.CreateHandler);
    }

    private static HttpResponseMessage Json(string payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    ///     Records what the probe actually sent and replays a scripted answer. It hands out a FRESH handler per call
    ///     because the probe owns and disposes the transport it is given.
    /// </summary>
    private sealed class ProbeTransport
    {
        private readonly Func<int, HttpResponseMessage> _responder;

        public ProbeTransport(Func<int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int RequestCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        public bool LastRequestHadAuthorization { get; private set; }

        public IReadOnlyDictionary<string, string> LastHeaders { get; private set; } = new Dictionary<string, string>();

        public HttpMessageHandler CreateHandler()
        {
            return new RecordingHandler(this);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly ProbeTransport _transport;

            public RecordingHandler(ProbeTransport transport)
            {
                _transport = transport;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _transport.LastRequestUri = request.RequestUri;
                _transport.LastAuthorization = request.Headers.Authorization?.ToString();
                _transport.LastRequestHadAuthorization = request.Headers.Contains("Authorization");
                _transport.LastHeaders = request.Headers.NonValidated.ToDictionary(static header => header.Key,
                    static header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase);
                var index = _transport.RequestCount++;
                return Task.FromResult(_transport._responder(index));
            }
        }
    }
}
