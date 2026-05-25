namespace XE_Local_AI_Engine.Tests.Endpoints.RuntimeManager;

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.RuntimeManager.V1;
using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimeManagerHubTests
{
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.Parse("2026-05-24T12:00:00Z");

    [Test]
    public async Task Negotiate_WhenTokenMissing_ReturnsUnauthorized()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/runtime/hub/negotiate?negotiateVersion=1")
        {
            Content = new StringContent(string.Empty)
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task StreamLogs_WhenAuthorized_StreamsHostAgentLogLines()
    {
        var managerService = Substitute.For<IHostAgentManagerService>();
        managerService.StreamLogsAsync("ollama", 25, true, Arg.Any<CancellationToken>())
                      .Returns(CreateLogStream("ollama"));
        await using var factory = CreateFactory(managerService);
        await using var connection = new HubConnectionBuilder()
                                      .WithUrl("http://localhost" + LocalApiRoutes.RuntimeManager.Hub, options =>
                                      {
                                          options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                          options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                                          options.Headers.Add("Origin", "http://localhost");
                                      })
                                     .Build();

        await connection.StartAsync().ConfigureAwait(false);

        var lines = new List<RuntimeLogLineResponse>();
        await foreach (var line in connection.StreamAsync<RuntimeLogLineResponse>("StreamLogs",
                           new RuntimeLogsRequest
                           {
                               ContainerName = " ollama ",
                               TailLines = 25,
                               Follow = true
                           }).ConfigureAwait(false))
        {
            lines.Add(line);
        }

        AssertEx.ContainsSingle(lines, line => line.ContainerName == "ollama" && line.Stream == "stdout" && line.Line == "ready");
        managerService.Received(1).StreamLogsAsync("ollama", 25, true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StreamLogs_WhenTailLinesTooHigh_ReturnsHubErrorBeforeHostAgentCall()
    {
        var managerService = Substitute.For<IHostAgentManagerService>();
        await using var factory = CreateFactory(managerService);
        await using var connection = new HubConnectionBuilder()
                                      .WithUrl("http://localhost" + LocalApiRoutes.RuntimeManager.Hub, options =>
                                      {
                                          options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                                          options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                                          options.Headers.Add("Origin", "http://localhost");
                                      })
                                     .Build();

        await connection.StartAsync().ConfigureAwait(false);

        var read = async () =>
        {
            await foreach (var line in connection.StreamAsync<RuntimeLogLineResponse>("StreamLogs",
                               new RuntimeLogsRequest
                               {
                                   ContainerName = "ollama",
                                   TailLines = 2_001,
                                   Follow = true
                               }).ConfigureAwait(false))
            {
                AssertEx.NotNull(line);
            }
        };

        await AssertEx.ThrowsAsync<Exception>(read);
        managerService.DidNotReceiveWithAnyArgs().StreamLogsAsync(default!, default, default, default);
    }

    private static TestingWebAppFactory CreateFactory(IHostAgentManagerService managerService)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IHostAgentManagerService>();
                services.AddSingleton(managerService);
            }
        };
    }

    private static async IAsyncEnumerable<HostAgentLogLineDto> CreateLogStream(string containerName,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new HostAgentLogLineDto
        {
            ContainerName = containerName,
            Stream = "stdout",
            Line = "ready",
            ObservedAt = FrozenNow
        };
    }
}
