namespace XE_Local_AI_Engine.Tests.Auth;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models.NodeBinding;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class NodeBindingServiceTests
{
    [Test]
    public async Task StartBindingAsync_WhenResponseIsSuccessful_ReturnsSession()
    {
        await using var service = CreateService(MockTokenStore.Unpaired(), request =>
        {
            AssertEx.Equal("/api/v1/client-nodes/device-bind/start", request.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(CreateStartResponse())
            });
        });

        var result = await service.StartBindingAsync();

        AssertEx.Equal("USER-CODE", result.UserCode);
        AssertEx.Equal(NodeBindingStatus.Pending, result.Status);
    }

    [Test]
    public async Task PollUntilTerminalAsync_WhenApproved_StoresCredentialsWithDeviceCodeMetadata()
    {
        var tokenStore = MockTokenStore.Unpaired();
        var pollCount = 0;
        var credentials = PairClientResponseBuilder.Valid().Build();
        await using var service = CreateService(tokenStore, request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/start", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(CreateStartResponse())
                });
            }

            pollCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new PollNodeBindingResponse
                {
                    Status = "approved",
                    IntervalSeconds = 1,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                    Credentials = credentials
                })
            });
        });

        var session = await service.StartBindingAsync();
        var result = await service.PollUntilTerminalAsync(session);

        AssertEx.Equal("approved", result.Status);
        AssertEx.Equal(expected: 1, pollCount);
        AssertEx.Equal(expected: 1, tokenStore.StoreTokensAsyncCallCount);
        AssertEx.Equal("device-code", tokenStore.BindingMethod);
        AssertEx.False(tokenStore.AutoConnectOnStart);
        AssertEx.Equal("worker-node-test", tokenStore.LastKnownNodeName);
    }

    [Test]
    public async Task PollUntilTerminalAsync_WhenExpired_DoesNotStoreCredentials()
    {
        var tokenStore = MockTokenStore.Unpaired();
        await using var service = CreateService(tokenStore, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PollNodeBindingResponse
            {
                Status = "expired",
                IntervalSeconds = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            })
        }));

        var result = await service.PollUntilTerminalAsync(new NodeBindingSession
        {
            DeviceCode = "device-code",
            UserCode = "USER-CODE",
            VerificationUri = "/client-nodes/device-bind",
            VerificationUriComplete = "/client-nodes/device-bind?user_code=USER-CODE",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            IntervalSeconds = 1
        });

        AssertEx.Equal("expired", result.Status);
        AssertEx.Equal(expected: 0, tokenStore.StoreTokensAsyncCallCount);
    }

    [Test]
    public async Task CancelAsync_CancelsActivePolling()
    {
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService(MockTokenStore.Unpaired(), request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) == true)
            {
                pollStarted.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PollNodeBindingResponse
                    {
                        Status = "pending",
                        IntervalSeconds = 1,
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
                    })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(CreateStartResponse())
            });
        });
        var session = await service.StartBindingAsync();

        var pollTask = service.PollUntilTerminalAsync(session);
        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await service.CancelAsync().ConfigureAwait(false);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => pollTask).ConfigureAwait(false);
    }

    private static NodeBindingService CreateService(MockTokenStore tokenStore,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("CentralPlatformApi").Returns(_ => new HttpClient(new DelegateHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("https://test.example.com")
        });

        return new NodeBindingService(httpClientFactory,
            tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = "https://test.example.com"
            }),
            Options.Create(new WorkerNodeOptions
            {
                NodeName = "worker-node-test"
            }),
            NullLogger<NodeBindingService>.Instance);
    }

    private static StartNodeBindingResponse CreateStartResponse()
    {
        return new StartNodeBindingResponse
        {
            DeviceCode = "device-code",
            UserCode = "USER-CODE",
            VerificationUri = "/client-nodes/device-bind",
            VerificationUriComplete = "/client-nodes/device-bind?user_code=USER-CODE",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            IntervalSeconds = 1
        };
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
