namespace XE_Local_AI_Engine.Tests.HostAgent;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class HostAgentManagerServiceTests
{
    private static readonly DateTimeOffset FrozenNow = DateTimeOffset.Parse("2026-05-20T12:00:00Z");

    [Test]
    public async Task LoadSnapshotAsync_WithMockHostAgent_ReturnsStatusModelsAndSanitizedManifest()
    {
        var hostAgent = CreateHostAgentClient();
        var localModelProvider = CreateModelProvider();
        var service = new HostAgentManagerService(hostAgent, localModelProvider, CreateConfigurationWithManifest());

        var snapshot = await service.LoadSnapshotAsync(CancellationToken.None);

        AssertEx.Equal(HostAgentState.Running, snapshot.Status.State);
        AssertEx.ContainsSingle(snapshot.Components, component => component.Name == "ollama");
        AssertEx.ContainsSingle(snapshot.Models, model => model.ModelName == "qwen3:0.6b");
        AssertEx.True(snapshot.Manifest.Available);
        AssertEx.Equal("managed", snapshot.Manifest.RuntimeMode);
        AssertEx.ContainsSingle(snapshot.Manifest.Containers, container => container.Name == "xe-node-web-server");

        var webServer = snapshot.Manifest.Containers.Single(container => container.Name == "xe-node-web-server");
        AssertEx.ContainsSingle(webServer.Environment,
            entry => entry.Name == "XE_HOST_AGENT_HMAC_SECRET_FILE" && entry.Value == "<redacted>");
    }

    [Test]
    public async Task ExecuteContainerActionAsync_WhenRestartRequested_CallsHostAgentRestart()
    {
        var hostAgent = CreateHostAgentClient();
        var service = new HostAgentManagerService(hostAgent, CreateModelProvider(), CreateConfigurationWithManifest());

        var report = await service.ExecuteContainerActionAsync("ollama",
            HostAgentContainerAction.Restart,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        AssertEx.True(report.Succeeded);
        await hostAgent.Received(1).RestartContainerAsync("ollama", TimeSpan.FromSeconds(30), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StreamLogsAsync_WithMockHostAgent_ReturnsLogLines()
    {
        var hostAgent = CreateHostAgentClient();
        hostAgent.StreamLogsAsync("ollama", 25, false, Arg.Any<CancellationToken>())
                 .Returns(CreateLogStream("ollama"));
        var service = new HostAgentManagerService(hostAgent, CreateModelProvider(), CreateConfigurationWithManifest());
        var lines = new List<HostAgentLogLineDto>();

        await foreach (var line in service.StreamLogsAsync("ollama", 25, false, CancellationToken.None))
        {
            lines.Add(line);
        }

        AssertEx.ContainsSingle(lines, line => line.ContainerName == "ollama" && line.Line == "ready");
    }

    [Test]
    public async Task IHostAgentManagerService_IsRegisteredInApplicationHost()
    {
        await using var factory = new TestingWebAppFactory();

        AssertEx.NotNull(factory.Services.GetRequiredService<IHostAgentManagerService>());
    }

    [Test]
    public async Task ManagerOverviewPage_DeclaresExpectedManagerRouteAndSections()
    {
        var page = await File.ReadAllTextAsync(GetClientPath("Components", "Pages", "Manager", "ManagerOverview.razor"));

        AssertEx.Contains(page, "@page \"/manager\"");
        AssertEx.Contains(page, "@attribute [Authorize(Roles = LocalOperatorAuthorization.OperatorRole)]");
        AssertEx.Contains(page, "IHostAgentManagerService");
        AssertEx.Contains(page, "Substrate status");
        AssertEx.Contains(page, "Model picker");
        AssertEx.Contains(page, "Start");
        AssertEx.Contains(page, "StreamLogsAsync");
        AssertEx.Contains(page, "Manifest");
    }

    private static IHostAgentClient CreateHostAgentClient()
    {
        var hostAgent = Substitute.For<IHostAgentClient>();
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

        hostAgent.GetStatusAsync(Arg.Any<CancellationToken>())
                 .Returns(new HostAgentStatusDto
                 {
                     State = HostAgentState.Running,
                     DesiredState = HostAgentDesiredState.Running,
                     RuntimeLifecycle = RuntimeLifecycle.Managed,
                     BootstrapModelReady = true,
                     WebUiUrl = "http://127.0.0.1:8080",
                     ObservedAt = FrozenNow,
                     Components = components,
                     Diagnostics = ["ok"]
                 });
        hostAgent.GetCapabilitiesAsync(Arg.Any<CancellationToken>())
                 .Returns(new HostCapabilitiesDto
                 {
                     CpuAvailable = true,
                     NvidiaGpuInference = false,
                     GpuRuntimeConfigured = false,
                     AmdGpuStatus = "not-detected",
                     RuntimeDiskBytes = 1_073_741_824,
                     ObservedAt = FrozenNow,
                     Diagnostics = []
                 });
        hostAgent.ListContainersAsync(Arg.Any<CancellationToken>()).Returns(components);
        hostAgent.RestartContainerAsync("ollama", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                 .Returns(new ContainerActionReportDto
                 {
                     Action = "restart",
                     Succeeded = true,
                     StartedAt = FrozenNow,
                     CompletedAt = FrozenNow.AddSeconds(1),
                     Components = components,
                     Diagnostics = []
                 });

        return hostAgent;
    }

    private static ILocalModelProvider CreateModelProvider()
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns("ollama");
        provider.CheckHealthAsync(Arg.Any<CancellationToken>())
                .Returns(new ModelProviderHealth
                {
                    ProviderName = "ollama",
                    IsHealthy = true,
                    ObservedAt = FrozenNow,
                    Diagnostics = []
                });
        provider.ListModelsAsync(Arg.Any<CancellationToken>())
                .Returns([
                    new LocalModelDescriptor
                    {
                        ModelName = "qwen3:0.6b",
                        ProviderName = "ollama",
                        IsAvailable = true,
                        SizeBytes = 512_000_000,
                        ModifiedAt = FrozenNow,
                        MaxContextTokens = 32_768
                    }
                ]);

        return provider;
    }

    private static IConfiguration CreateConfigurationWithManifest()
    {
        return new ConfigurationBuilder()
               .AddInMemoryCollection(new Dictionary<string, string?>
               {
                   ["HostAgent:Runtime:Manifest:SchemaVersion"] = "1",
                   ["HostAgent:Runtime:Manifest:RuntimeMode"] = "managed",
                   ["HostAgent:Runtime:Manifest:Models:BootstrapModel"] = "qwen3:0.6b",
                   ["HostAgent:Runtime:Manifest:Models:DefaultChatModel"] = "qwen3:8b",
                   ["HostAgent:Runtime:Manifest:Containers:0:Name"] = "ollama",
                   ["HostAgent:Runtime:Manifest:Containers:0:Image"] = "ollama/ollama:0.11.10@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                   ["HostAgent:Runtime:Manifest:Containers:0:Network"] = "xe-engine-net",
                   ["HostAgent:Runtime:Manifest:Containers:0:Environment:OLLAMA_KEEP_ALIVE"] = "10m",
                   ["HostAgent:Runtime:Manifest:Containers:0:Volumes:0:Source"] = "ollama-models",
                   ["HostAgent:Runtime:Manifest:Containers:0:Volumes:0:Target"] = "/root/.ollama",
                   ["HostAgent:Runtime:Manifest:Containers:0:Volumes:0:ReadOnly"] = "false",
                   ["HostAgent:Runtime:Manifest:Containers:1:Name"] = "xe-node-web-server",
                   ["HostAgent:Runtime:Manifest:Containers:1:Image"] = "ghcr.io/c0re/xe-local-ai-engine:0.1.0@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                   ["HostAgent:Runtime:Manifest:Containers:1:Network"] = "xe-engine-net",
                   ["HostAgent:Runtime:Manifest:Containers:1:Environment:XE_HOST_AGENT_HMAC_SECRET_FILE"] = "/etc/host-agent/hmac-secret",
                   ["HostAgent:Runtime:Manifest:Containers:1:Volumes:0:Source"] = "/etc/xe-host-agent/hmac-secret",
                   ["HostAgent:Runtime:Manifest:Containers:1:Volumes:0:Target"] = "/etc/host-agent/hmac-secret",
                   ["HostAgent:Runtime:Manifest:Containers:1:Volumes:0:ReadOnly"] = "true",
                   ["HostAgent:Runtime:Manifest:RuntimeLimits:MaxRuntimeDiskGb"] = "128",
                   ["HostAgent:Runtime:Manifest:RuntimeLimits:StopDrainTimeoutSeconds"] = "30"
               })
               .Build();
    }

    private static async IAsyncEnumerable<HostAgentLogLineDto> CreateLogStream(string containerName)
    {
        await Task.Yield();
        yield return new HostAgentLogLineDto
        {
            ContainerName = containerName,
            Stream = "stdout",
            Line = "ready",
            ObservedAt = FrozenNow
        };
    }

    private static string GetClientPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine([
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "XE-Local-AI-Engine.Client",
            ..segments
        ]));
    }
}
