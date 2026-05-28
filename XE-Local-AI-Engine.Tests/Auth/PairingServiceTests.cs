namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class PairingServiceTests
{
    [Test]
    public async Task PairAsync_WhenResponseIsSuccessful_StoresTokensAndReturnsResponse()
    {
        var expected = PairClientResponseBuilder.Valid().Build();
        var tokenStore = MockTokenStore.Unpaired();
        var service = CreateService(tokenStore, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(expected)
        }));

        var result = await service.PairAsync("pair-token");

        AssertEx.Equal(expected.ClientNodeId, result.ClientNodeId);
        AssertEx.Equal(1, tokenStore.StoreTokensAsyncCallCount);
    }

    [Test]
    public async Task PairAsync_WhenStatusIsUnauthorized_ThrowsPairingTokenInvalidException()
    {
        var service = CreateService(MockTokenStore.Unpaired(), _ => CreateResponse(HttpStatusCode.Unauthorized));

        await AssertEx.ThrowsAsync<PairingTokenInvalidException>(() => service.PairAsync("pair-token"));
    }

    [Test]
    public async Task PairAsync_WhenStatusIsForbidden_ThrowsPairingTokenInvalidException()
    {
        var service = CreateService(MockTokenStore.Unpaired(), _ => CreateResponse(HttpStatusCode.Forbidden));

        await AssertEx.ThrowsAsync<PairingTokenInvalidException>(() => service.PairAsync("pair-token"));
    }

    [Test]
    public async Task PairAsync_WhenStatusIsConflict_ThrowsPairingTokenUsedException()
    {
        var service = CreateService(MockTokenStore.Unpaired(), _ => CreateResponse(HttpStatusCode.Conflict));

        await AssertEx.ThrowsAsync<PairingTokenUsedException>(() => service.PairAsync("pair-token"));
    }

    [Test]
    public async Task PairAsync_WhenStatusIsGone_ThrowsPairingTokenExpiredException()
    {
        var service = CreateService(MockTokenStore.Unpaired(), _ => CreateResponse(HttpStatusCode.Gone));

        await AssertEx.ThrowsAsync<PairingTokenExpiredException>(() => service.PairAsync("pair-token"));
    }

    [Test]
    public async Task PairAsync_WhenStatusIsServerError_ThrowsPairingException()
    {
        var service = CreateService(MockTokenStore.Unpaired(), _ => CreateResponse(HttpStatusCode.InternalServerError));

        await AssertEx.ThrowsAsync<PairingException>(() => service.PairAsync("pair-token"));
    }

    [Test]
    public async Task PairAsync_WhenResponseBodyIsNull_ThrowsPairingException()
    {
        var service = CreateService(MockTokenStore.Unpaired(), _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null")
        }));

        await AssertEx.ThrowsAsync<PairingException>(() => service.PairAsync("pair-token"));
    }

    [Test]
    public async Task PairAsync_WhenCalled_SendsConfiguredNodeNameAndToken()
    {
        PairClientRequest? captured = null;
        var response = PairClientResponseBuilder.Valid().Build();
        var service = CreateService(MockTokenStore.Unpaired(),
            async request =>
            {
                captured = await request.Content!.ReadFromJsonAsync<PairClientRequest>();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(response)
                };
            });

        await service.PairAsync("pair-token");

        var pairRequest = AssertEx.NotNull(captured);
        AssertEx.Equal("worker-node-test", pairRequest.NodeName);
        AssertEx.Equal("pair-token", pairRequest.Token);
    }

    [Test]
    public async Task UnpairAsync_WhenCalled_ClearsTokenStore()
    {
        var tokenStore = MockTokenStore.Unpaired();
        var service = CreateService(tokenStore, _ => throw new InvalidOperationException("Not used."));

        await service.UnpairAsync();

        AssertEx.Equal(1, tokenStore.ClearTokensAsyncCallCount);
    }

    private static PairingService CreateService(ITokenStore tokenStore,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("CentralPlatformApi").Returns(_ => new HttpClient(new DelegateHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("https://test.example.com")
        });

        return new PairingService(httpClientFactory,
            tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = "https://test.example.com"
            }),
            Options.Create(new WorkerNodeOptions
            {
                NodeName = "worker-node-test"
            }),
            NullLogger<PairingService>.Instance);
    }

    private static Task<HttpResponseMessage> CreateResponse(HttpStatusCode statusCode)
    {
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                error = statusCode.ToString()
            }))
        });
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _handler(request);
        }
    }
}
