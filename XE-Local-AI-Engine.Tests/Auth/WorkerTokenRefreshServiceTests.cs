namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class WorkerTokenRefreshServiceTests
{
    [Test]
    public async Task TryRefreshAsync_WhenStatusIsUnauthorized_ReturnsCredentialsRevoked()
    {
        var tokenStore = MockTokenStore.Paired("access", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var service = CreateService(tokenStore, _ => CreateResponse(HttpStatusCode.Unauthorized));

        var outcome = await service.TryRefreshAsync();

        AssertEx.Equal(WorkerTokenRefreshOutcome.CredentialsRevoked, outcome);
        AssertEx.Equal(1, tokenStore.ClearTokensAsyncCallCount);
    }

    [Test]
    public async Task TryRefreshAsync_WhenStatusIsForbidden_ReturnsCredentialsRevoked()
    {
        var tokenStore = MockTokenStore.Paired("access", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var service = CreateService(tokenStore, _ => CreateResponse(HttpStatusCode.Forbidden));

        var outcome = await service.TryRefreshAsync();

        AssertEx.Equal(WorkerTokenRefreshOutcome.CredentialsRevoked, outcome);
        AssertEx.Equal(1, tokenStore.ClearTokensAsyncCallCount);
    }

    [Test]
    public async Task TryRefreshAsync_WhenStatusIsNotFound_ReturnsCredentialsRevoked()
    {
        var tokenStore = MockTokenStore.Paired("access", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var service = CreateService(tokenStore, _ => CreateResponse(HttpStatusCode.NotFound));

        var outcome = await service.TryRefreshAsync();

        AssertEx.Equal(WorkerTokenRefreshOutcome.CredentialsRevoked, outcome);
        AssertEx.Equal(1, tokenStore.ClearTokensAsyncCallCount);
    }

    [Test]
    public async Task TryRefreshAsync_WhenRefreshTokenMetadataMissing_ReturnsCredentialsRevoked()
    {
        var tokenStore = MockTokenStore.Unpaired();
        var service = CreateService(tokenStore, _ => throw new InvalidOperationException("HTTP must not be called when refresh metadata is missing."));

        var outcome = await service.TryRefreshAsync();

        AssertEx.Equal(WorkerTokenRefreshOutcome.CredentialsRevoked, outcome);
    }

    [Test]
    public async Task TryRefreshAsync_WhenStatusIsServiceUnavailable_ReturnsTransientFailure()
    {
        var tokenStore = MockTokenStore.Paired("access", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var service = CreateService(tokenStore, _ => CreateResponse(HttpStatusCode.ServiceUnavailable));

        var outcome = await service.TryRefreshAsync();

        AssertEx.Equal(WorkerTokenRefreshOutcome.TransientFailure, outcome);
        AssertEx.Equal(0, tokenStore.ClearTokensAsyncCallCount);
    }

    [Test]
    public async Task TryRefreshAsync_WhenHttpRequestThrows_ReturnsTransientFailure()
    {
        var tokenStore = MockTokenStore.Paired("access", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var service = CreateService(tokenStore, _ => throw new HttpRequestException("network down"));

        var outcome = await service.TryRefreshAsync();

        AssertEx.Equal(WorkerTokenRefreshOutcome.TransientFailure, outcome);
        AssertEx.Equal(0, tokenStore.ClearTokensAsyncCallCount);
    }

    [Test]
    public async Task TryRefreshAsync_WhenResponseBodyIsNull_ReturnsTransientFailure()
    {
        var tokenStore = MockTokenStore.Paired("access", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var service = CreateService(tokenStore, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null")
        }));

        var outcome = await service.TryRefreshAsync();

        AssertEx.Equal(WorkerTokenRefreshOutcome.TransientFailure, outcome);
    }

    [Test]
    public async Task TryRefreshAsync_WhenStatusIsSuccessWithCredentials_ReturnsSuccessAndStoresTokens()
    {
        var tokenStore = MockTokenStore.Paired("access", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1));
        var refreshed = new PairClientResponse
        {
            ClientNodeId = Guid.NewGuid(),
            AccessToken = "new-access-token",
            RefreshToken = "new-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var service = CreateService(tokenStore, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(refreshed)
        }));

        var outcome = await service.TryRefreshAsync();

        AssertEx.Equal(WorkerTokenRefreshOutcome.Success, outcome);
        AssertEx.Equal(1, tokenStore.StoreTokensAsyncCallCount);
        AssertEx.Equal("new-access-token", await tokenStore.GetAccessTokenAsync());
    }

    private static WorkerTokenRefreshService CreateService(ITokenStore tokenStore,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("CentralPlatformApi").Returns(_ => new HttpClient(new DelegateHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("https://test.example.com")
        });

        return new WorkerTokenRefreshService(httpClientFactory,
            tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = "https://test.example.com"
            }),
            NullLogger<WorkerTokenRefreshService>.Instance);
    }

    private static Task<HttpResponseMessage> CreateResponse(HttpStatusCode statusCode)
    {
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(statusCode.ToString())
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
