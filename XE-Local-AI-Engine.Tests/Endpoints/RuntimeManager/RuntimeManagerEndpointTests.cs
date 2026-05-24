namespace XE_Local_AI_Engine.Tests.Endpoints.RuntimeManager;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.RuntimeManager.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimeManagerEndpointTests
{
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.Parse("2026-05-24T12:00:00Z");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Test]
    public async Task GetRuntimeStatus_WhenAuthorized_ReturnsSnapshotWithoutManifestSecrets()
    {
        var managerService = Substitute.For<IHostAgentManagerService>();
        managerService.LoadSnapshotAsync(Arg.Any<CancellationToken>()).Returns(CreateSnapshot());
        await using var factory = CreateFactory(managerService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/runtime/status");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var status = Deserialize<RuntimeManagerStatusResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal(HostAgentState.Running, status.Status.State);
        AssertEx.True(status.Capabilities.CpuAvailable);
        AssertEx.ContainsSingle(status.Components, component => component.Name == "ollama" && component.Health == ContainerHealth.Healthy);
        AssertEx.ContainsSingle(status.Manifest.Containers, container => container.Name == "xe-node-web-server");
        AssertEx.ContainsSingle(status.Manifest.Containers.Single(container => container.Name == "xe-node-web-server").Environment,
            entry => entry.Name == "XE_HOST_AGENT_HMAC_SECRET_FILE" && entry.Value == "<redacted>");
        AssertEx.False(body.Contains("super-secret-hmac-value", StringComparison.OrdinalIgnoreCase));
        await managerService.Received(1).LoadSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetRuntimeStatus_WhenMissingLocalOperatorToken_ReturnsUnauthorized()
    {
        var managerService = Substitute.For<IHostAgentManagerService>();
        await using var factory = CreateFactory(managerService);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/local/v1/runtime/status").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await managerService.DidNotReceiveWithAnyArgs().LoadSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteContainerAction_WhenStartRequested_CallsManagerService()
    {
        await AssertContainerActionAsync("start", HostAgentContainerAction.Start).ConfigureAwait(false);
    }

    [Test]
    public async Task ExecuteContainerAction_WhenStopRequested_CallsManagerService()
    {
        await AssertContainerActionAsync("stop", HostAgentContainerAction.Stop).ConfigureAwait(false);
    }

    [Test]
    public async Task ExecuteContainerAction_WhenRestartRequested_CallsManagerService()
    {
        await AssertContainerActionAsync("restart", HostAgentContainerAction.Restart).ConfigureAwait(false);
    }

    [Test]
    public async Task ExecuteContainerAction_WhenActionInvalid_ReturnsValidationProblem()
    {
        var managerService = Substitute.For<IHostAgentManagerService>();
        await using var factory = CreateFactory(managerService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/runtime/containers/action");
        request.Content = JsonContent.Create(new RuntimeContainerActionRequest
        {
            ContainerName = "ollama",
            Action = "destroy"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await managerService.DidNotReceiveWithAnyArgs().ExecuteContainerActionAsync(Arg.Any<string>(), Arg.Any<HostAgentContainerAction>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteContainerAction_WhenMissingLocalOperatorToken_ReturnsUnauthorized()
    {
        var managerService = Substitute.For<IHostAgentManagerService>();
        await using var factory = CreateFactory(managerService);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/local/v1/runtime/containers/action", new RuntimeContainerActionRequest
        {
            ContainerName = "ollama",
            Action = "restart"
        }).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await managerService.DidNotReceiveWithAnyArgs().ExecuteContainerActionAsync(Arg.Any<string>(), Arg.Any<HostAgentContainerAction>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
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

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        var token = factory.Services.GetRequiredService<ILocalOperatorTokenProvider>().Token;
        request.Headers.Add(LocalOperatorAuthorization.HeaderName, token);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task AssertContainerActionAsync(string actionName, HostAgentContainerAction expectedAction)
    {
        var managerService = Substitute.For<IHostAgentManagerService>();
        managerService.ExecuteContainerActionAsync("ollama", expectedAction, TimeSpan.FromSeconds(45), Arg.Any<CancellationToken>())
                      .Returns(CreateActionReport(actionName));
        await using var factory = CreateFactory(managerService);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Post, "/api/local/v1/runtime/containers/action");
        request.Content = JsonContent.Create(new RuntimeContainerActionRequest
        {
            ContainerName = " ollama ",
            Action = actionName.ToUpperInvariant(),
            DrainTimeoutSeconds = 45
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var action = await ReadJsonAsync<RuntimeContainerActionResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("ollama", action.ContainerName);
        AssertEx.Equal(actionName, action.Action);
        AssertEx.True(action.Succeeded);
        await managerService.Received(1).ExecuteContainerActionAsync("ollama", expectedAction, TimeSpan.FromSeconds(45), Arg.Any<CancellationToken>());
    }

    private static ContainerActionReportDto CreateActionReport(string action)
    {
        return new ContainerActionReportDto
        {
            Action = action,
            Succeeded = true,
            StartedAt = FrozenNow.AddSeconds(-2),
            CompletedAt = FrozenNow,
            Components = CreateSnapshot().Components,
            Diagnostics = [$"{action}:ok"]
        };
    }

    private static HostAgentManagerSnapshot CreateSnapshot()
    {
        var components = new[]
        {
            new RuntimeComponentStatusDto
            {
                Name = "ollama",
                DesiredState = ContainerDesiredState.Running,
                Health = ContainerHealth.Healthy,
                ImageReference = "ollama/ollama:0.11.10@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                DigestVerified = true,
                ObservedAt = FrozenNow,
                Diagnostics = ["healthy"]
            }
        };

        return new HostAgentManagerSnapshot(
            new HostAgentStatusDto
            {
                State = HostAgentState.Running,
                DesiredState = HostAgentDesiredState.Running,
                RuntimeLifecycle = RuntimeLifecycle.Managed,
                BootstrapModelReady = true,
                WebUiUrl = "http://127.0.0.1:8080",
                ObservedAt = FrozenNow,
                Components = components,
                Diagnostics = ["ok"]
            },
            new HostCapabilitiesDto
            {
                CpuAvailable = true,
                NvidiaGpuInference = false,
                GpuRuntimeConfigured = false,
                AmdGpuStatus = "not-detected",
                RuntimeDiskBytes = 1_073_741_824,
                ObservedAt = FrozenNow,
                Diagnostics = []
            },
            components,
            new ModelProviderHealth
            {
                ProviderName = "ollama",
                IsHealthy = true,
                ObservedAt = FrozenNow,
                Diagnostics = []
            },
            [
                new LocalModelDescriptor
                {
                    ModelName = "qwen3:0.6b",
                    ProviderName = "ollama",
                    IsAvailable = true,
                    SizeBytes = 512_000_000,
                    ModifiedAt = FrozenNow,
                    MaxContextTokens = 32_768
                }
            ],
            new HostAgentManifestView(true,
                1,
                "managed",
                "qwen3:0.6b",
                "qwen3:8b",
                128,
                30,
                [
                    new HostAgentManifestContainerView("xe-node-web-server",
                        "ghcr.io/c0re/xe-local-ai-engine:0.1.0@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        "xe-engine-net",
                        [new HostAgentManifestEnvironmentView("XE_HOST_AGENT_HMAC_SECRET_FILE", "<redacted>")],
                        [new HostAgentManifestVolumeView("/etc/xe-host-agent/hmac-secret", "/etc/host-agent/hmac-secret", true)])
                ],
                []));
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(json, JsonOptions));
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return Deserialize<T>(body);
    }
}
