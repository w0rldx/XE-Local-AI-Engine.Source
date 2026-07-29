namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Decision D10 — daemon attestation — and the operator-facing preflight built on it.
///     <para>
///         Two properties are being defended, and they pull in opposite directions. The control must notice a
///         substituted daemon, because <c>DOCKER_HOST</c> is an ordinary environment variable and a substituted daemon
///         is a substituted execution host for the operator's repository. It must equally not cry wolf, because a
///         warning that fires when nothing is wrong is a warning people learn to click through — so a daemon that
///         merely moved sockets must stay silent.
///     </para>
/// </summary>
public sealed class DockerDaemonPreflightServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 7, day: 29, hour: 9, minute: 30, second: 0, TimeSpan.Zero);

    [Test]
    public async Task InspectAsync_OnFirstUse_PinsTheDaemonAndReportsReady()
    {
        var (service, _, store) = CreateService();

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, preflight.Status);
        AssertEx.True(preflight.Ready);
        var pinned = AssertEx.NotNull(await store.ReadAsync());
        AssertEx.Equal("daemon-alpha", pinned.DaemonId);
        AssertEx.Equal(FixedNow, pinned.ConfirmedAtUtc);
        // Recorded honestly as a first-use pin rather than as an operator decision, so an operator can tell
        // "this node has never been asked" from "this node was asked and answered".
        AssertEx.False(pinned.ConfirmedByOperator);
    }

    [Test]
    public async Task InspectAsync_WhenTheSameDaemonAnswers_StaysReadyWithoutRewritingTheApproval()
    {
        var (service, _, store) = CreateService();
        await service.InspectAsync();

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, preflight.Status);
        AssertEx.Equal(FixedNow, AssertEx.NotNull(await store.ReadAsync()).ConfirmedAtUtc);
    }

    [Test]
    public async Task InspectAsync_WhenTheSameDaemonMovedSockets_DoesNotCryWolf()
    {
        // The false-positive direction. Identity is what is pinned; the endpoint is context. A daemon that moved is
        // the same daemon, and prompting for it would train the operator to approve prompts without reading them.
        var (service, client, _) = CreateService();
        await service.InspectAsync();

        var movedEndpoint = new DockerDaemonEndpoint(new Uri("unix:///run/user/1000/docker.sock"),
            DockerDaemonEndpointSource.UserRuntimeUnixSocket);
        client.Identity = client.Identity with { Endpoint = movedEndpoint };

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, (await service.InspectAsync()).Status);
    }

    [Test]
    public async Task InspectAsync_WhenADifferentDaemonAnswers_RefusesAndDoesNotRepinIt()
    {
        var (service, client, store) = CreateService();
        await service.InspectAsync();
        client.Identity = client.Identity with { DaemonId = "daemon-beta", ServerVersion = "28.0.0" };

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.DaemonIdentityChanged, preflight.Status);
        AssertEx.False(preflight.Ready);
        AssertEx.True(preflight.RequiresOperatorConfirmation);
        // The whole control: a read must never approve. Trust-on-first-use pins once; after that only an operator
        // moves the pin.
        AssertEx.Equal("daemon-alpha", AssertEx.NotNull(await store.ReadAsync()).DaemonId);
    }

    [Test]
    public async Task InspectAsync_WhenADifferentDaemonAnswers_NamesBothRuntimesAndWhyItMatters()
    {
        var (service, client, _) = CreateService();
        await service.InspectAsync();
        client.Identity = client.Identity with { DaemonId = "daemon-beta", ServerVersion = "28.0.0" };

        var message = (await service.InspectAsync()).Message;

        AssertEx.Contains(message, "daemon-alpha");
        AssertEx.Contains(message, "daemon-beta");
        AssertEx.Contains(message, "DOCKER_HOST");
        AssertEx.Contains(message, "Confirm");
    }

    [Test]
    public async Task ConfirmAsync_WithTheRuntimeTheOperatorWasShown_PinsItAndReportsReady()
    {
        var (service, client, store) = CreateService();
        await service.InspectAsync();
        client.Identity = client.Identity with { DaemonId = "daemon-beta" };

        var preflight = await service.ConfirmAsync("daemon-beta");

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, preflight.Status);
        var pinned = AssertEx.NotNull(await store.ReadAsync());
        AssertEx.Equal("daemon-beta", pinned.DaemonId);
        AssertEx.True(pinned.ConfirmedByOperator);
    }

    [Test]
    public async Task ConfirmAsync_WhenTheRuntimeChangedAgainBeforeTheConfirmationLanded_ApprovesNothing()
    {
        // Without this check a confirmation issued against one runtime would land on whichever answered next, and the
        // control would approve something nobody looked at — worse than having no confirmation step, because it looks
        // like one.
        var (service, client, store) = CreateService();
        await service.InspectAsync();
        client.Identity = client.Identity with { DaemonId = "daemon-gamma" };

        var preflight = await service.ConfirmAsync("daemon-beta");

        AssertEx.Equal(DockerDaemonPreflightStatus.DaemonIdentityChanged, preflight.Status);
        AssertEx.Contains(preflight.Message, "Nothing was approved");
        AssertEx.Equal("daemon-alpha", AssertEx.NotNull(await store.ReadAsync()).DaemonId);
    }

    [Test]
    public async Task InspectAsync_WhenNoDaemonIsReachable_SaysSoAndSaysThereIsNoFallback()
    {
        var (service, client, _) = CreateService();
        client.ProbeFailure = new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
            "No Docker socket exists at '/var/run/docker.sock'.");

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.DaemonUnreachable, preflight.Status);
        AssertEx.False(preflight.RequiresOperatorConfirmation);
        AssertEx.Contains(preflight.Message, "Start the Docker daemon");
        AssertEx.Contains(preflight.Message, "DOCKER_HOST");
        // ADR 0004's consequence stated to the person it affects, not just in the record.
        AssertEx.Contains(preflight.Message, "no unisolated fallback");
    }

    [Test]
    public async Task InspectAsync_WhenTheSocketRefusesUs_SaysWhatGrantingAccessActuallyGrants()
    {
        var (service, client, _) = CreateService();
        client.ProbeFailure = new DockerRuntimeException(DockerDaemonPreflightStatus.PermissionDenied, "denied");

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.PermissionDenied, preflight.Status);
        AssertEx.Contains(preflight.Message, "equivalent to root");
        // Documented, not mitigated (ADR 0004). And rootless Docker is the user's own option, so the copy must not
        // imply the product requires or provides it.
        AssertEx.False(preflight.Message.Contains("rootless", StringComparison.OrdinalIgnoreCase),
            "The permission-denied message must not imply rootless Docker is required or provided.");
    }

    [Test]
    public async Task InspectAsync_WhenTheDaemonApiIsTooOld_SaysWhichVersionsAndWhy()
    {
        var (service, client, _) = CreateService();
        client.Identity = client.Identity with { ApiVersion = "1.40" };

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.ApiVersionTooOld, preflight.Status);
        AssertEx.Contains(preflight.Message, "1.40");
        AssertEx.Contains(preflight.Message, "1.41");
        AssertEx.Contains(preflight.Message, "Upgrade Docker Engine");
    }

    [Test]
    public async Task InspectAsync_ComparesApiVersionsComponentWiseRatherThanAsDecimals()
    {
        // 1.9 precedes 1.41. Read as decimals, 1.9 > 1.41 and an ancient daemon would pass the gate.
        var (service, client, _) = CreateService();
        client.Identity = client.Identity with { ApiVersion = "1.9" };

        AssertEx.Equal(DockerDaemonPreflightStatus.ApiVersionTooOld, (await service.InspectAsync()).Status);
    }

    [Test]
    public async Task InspectAsync_WhenNoApprovedImageIsConfigured_SaysSoWithoutBlamingTheDaemon()
    {
        var (service, _, _) = CreateService(DockerSandboxHardeningTests.Options() with { Image = null });

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.NotConfigured, preflight.Status);
        AssertEx.Contains(preflight.Message, "digest-pinned");
    }

    [Test]
    public async Task InspectAsync_WhenReady_TellsTheOperatorWhichRuntimeAndHowItWasFound()
    {
        var (service, _, _) = CreateService();

        var preflight = await service.InspectAsync();

        AssertEx.Contains(preflight.Message, "29.6.1");
        AssertEx.Contains(preflight.Message, "daemon-alpha");
        AssertEx.Contains(preflight.Message, "this node's configuration");
    }

    private static (IDockerDaemonPreflightService Service, FakeDockerRuntimeClient Client, InMemoryAttestationStore Store) CreateService(
        ContainerSandboxOptions? options = null)
    {
        var resolved = options ?? DockerSandboxHardeningTests.Options() with { DaemonEndpoint = "unix:///fake.sock" };
        var endpoint = new DockerDaemonEndpoint(new Uri("unix:///fake.sock"), DockerDaemonEndpointSource.Configuration);
        var client = new FakeDockerRuntimeClient(endpoint,
            new DockerDaemonIdentity("daemon-alpha", "29.6.1", "1.55", "1.40", "linux", endpoint));
        var store = new InMemoryAttestationStore();

        var service = new DockerDaemonPreflightService(new StaticOptionsMonitor<ContainerSandboxOptions>(resolved),
            new SingleClientFactory(client),
            store,
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerDaemonPreflightService>.Instance);

        return (service, client, store);
    }

    private sealed class SingleClientFactory : IDockerRuntimeClientFactory
    {
        private readonly FakeDockerRuntimeClient _client;

        public SingleClientFactory(FakeDockerRuntimeClient client)
        {
            _client = client;
        }

        public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint)
        {
            return _client;
        }
    }

    private sealed class InMemoryAttestationStore : IDockerDaemonAttestationStore
    {
        private DockerDaemonAttestation? _attestation;

        public Task<DockerDaemonAttestation?> ReadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_attestation);
        }

        public Task WriteAsync(DockerDaemonAttestation attestation, CancellationToken cancellationToken = default)
        {
            _attestation = attestation;
            return Task.CompletedTask;
        }
    }
}
