namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Daemon attestation and the operator-facing preflight built on it.
///     <para>
///         Two properties are being defended, and they pull in opposite directions. The control must notice a
///         substituted daemon, because <c>DOCKER_HOST</c> is an ordinary environment variable and a substituted daemon
///         is a substituted execution host for the operator's repository. It must equally not cry wolf, because a
///         warning that fires when nothing is wrong is a warning people learn to click through — so a daemon that
///         merely moved sockets must stay silent.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DockerDaemonPreflightServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 7, day: 29, hour: 9, minute: 30, second: 0, TimeSpan.Zero);

    /// <summary>Every value an operator can hide in an endpoint, in the three components that can hold one.</summary>
    private static readonly string[] Secrets = ["hunter2", "sekrit-9f3a", "frag-sentinel-4d1c"];

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

        var movedEndpoint = new DockerDaemonEndpoint
        {
            Uri = new Uri("unix:///run/user/1000/docker.sock"),
            Source = DockerDaemonEndpointSource.UserRuntimeUnixSocket
        };
        client.Identity = client.Identity with
        {
            Endpoint = movedEndpoint
        };

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, (await service.InspectAsync()).Status);
    }

    [Test]
    public async Task InspectAsync_WhenADifferentDaemonAnswers_RefusesAndDoesNotRepinIt()
    {
        var (service, client, store) = CreateService();
        await service.InspectAsync();
        client.Identity = client.Identity with
        {
            DaemonId = "daemon-beta",
            ServerVersion = "28.0.0"
        };

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
        client.Identity = client.Identity with
        {
            DaemonId = "daemon-beta",
            ServerVersion = "28.0.0"
        };

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
        client.Identity = client.Identity with
        {
            DaemonId = "daemon-beta"
        };

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
        client.Identity = client.Identity with
        {
            DaemonId = "daemon-gamma"
        };

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
        client.Identity = client.Identity with
        {
            ApiVersion = "1.40"
        };

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
        client.Identity = client.Identity with
        {
            ApiVersion = "1.9"
        };

        AssertEx.Equal(DockerDaemonPreflightStatus.ApiVersionTooOld, (await service.InspectAsync()).Status);
    }

    [Test]
    public async Task InspectAsync_WhenNoApprovedImageIsConfigured_SaysSoWithoutBlamingTheDaemon()
    {
        var (service, _, _) = CreateService(DockerSandboxHardeningTests.Options() with
        {
            Image = null
        });

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.NotConfigured, preflight.Status);
        AssertEx.Contains(preflight.Message, "digest-pinned");
    }

    [Test]
    public async Task InspectAsync_WhenReady_TellsTheOperatorWhichRuntimeAndHowItWasFound()
    {
        var (service, _, _) = CreateService();

        var preflight = await service.InspectAsync();

        AssertEx.Contains(preflight.Message, "99.0.0");
        AssertEx.Contains(preflight.Message, "daemon-alpha");
        AssertEx.Contains(preflight.Message, "this node's configuration");
    }

    [Test]
    public async Task InspectAsync_WhenTheDaemonDoesNotReportSeccomp_RefusesAndSaysWhatToCheck()
    {
        // Checked here because it cannot be checked at create time: such a daemon still accepts a seccomp profile and
        // still reports it back on the container while applying nothing, so the read-back is indistinguishable from a
        // confining daemon's. `docker info`'s security options are the only place the difference is visible.
        var (service, client, store) = CreateService();
        client.Identity = client.Identity with
        {
            SupportsSeccomp = false
        };

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.ProbeFailed, preflight.Status);
        AssertEx.False(preflight.Ready);
        AssertEx.Contains(preflight.Message, "seccomp");
        AssertEx.Contains(preflight.Message, "docker info");
        AssertEx.Contains(preflight.Message, "does not fall back");
        // Refused rather than pinned: a daemon this node will not use must not become the daemon it approved.
        AssertEx.Null(await store.ReadAsync());
    }

    [Test]
    public void SeccompProfile_IsPresentInThisBuildAndNamesARealProfile()
    {
        // The other half of the preflight's asset check. Its failure branch cannot be reached from a test — the
        // profile is an embedded resource, so "missing" means a broken build rather than a state a test can create —
        // so what is asserted is the success side: this build really carries a profile, and the predicate the
        // read-back uses really accepts it.
        var option = DockerSandboxHardeningTests.Specification()
                                                .SecurityOptions
                                                .First(entry => entry.StartsWith("seccomp=", StringComparison.Ordinal));

        AssertEx.Contains(option, "SCMP_ACT_ERRNO");
    }

    [Test]
    public async Task InspectAsync_WhenThePinOnDiskCarriesCredentials_NamesTheHostAndNeverTheSecret()
    {
        // The case the write-side redaction cannot reach. New pins are written through DockerDaemonEndpoint.Display,
        // but a pin written before that existed holds whatever DOCKER_HOST held — and tcp://user:secret@host is a
        // value an operator can set. It comes back off disk into the identity-change message and into the Development
        // Mode API response, so it has to be redacted on the way IN: refusing an endpoint while echoing its
        // credential discloses the secret in the course of declining to use it.
        var root = Path.Combine(Path.GetTempPath(), "xe-daemon-attestation-" + Guid.NewGuid().ToString("N"));

        try
        {
            var file = Path.Combine(root, DockerDaemonAttestationStore.DirectoryName, DockerDaemonAttestationStore.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file,
                """
                {
                  "daemonId": "daemon-legacy",
                  "endpoint": "tcp://operator:hunter2@docker.remote:2375/?token=sekrit-9f3a#frag-sentinel-4d1c",
                  "endpointSource": 1,
                  "serverVersion": "28.0.0",
                  "confirmedAtUtc": "2026-07-01T00:00:00+00:00",
                  "confirmedByOperator": true
                }
                """);

            using var store = new DockerDaemonAttestationStore(new FakeNodeDataDirectory(root),
                NullLogger<DockerDaemonAttestationStore>.Instance);

            var pinned = AssertEx.NotNull(await store.ReadAsync());
            AssertEx.Contains(pinned.Endpoint, "docker.remote:2375");
            foreach (var secret in Secrets)
            {
                AssertEx.False(pinned.Endpoint.Contains(secret, StringComparison.Ordinal),
                    $"The pin came back off disk carrying '{secret}': '{pinned.Endpoint}'.");
            }

            var (service, _, _) = CreateService(attestationStore: store);

            // daemon-alpha answers and the pin names daemon-legacy, which is the one state that renders the pinned
            // endpoint to the operator.
            var preflight = await service.InspectAsync();

            AssertEx.Equal(DockerDaemonPreflightStatus.DaemonIdentityChanged, preflight.Status);
            AssertEx.Contains(preflight.Message, "docker.remote:2375");
            foreach (var secret in Secrets)
            {
                AssertEx.False(preflight.Message.Contains(secret, StringComparison.Ordinal),
                    $"The identity-change message disclosed '{secret}', which it is refusing to use: {preflight.Message}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments("unix:///fake.sock?token=sekrit-9f3a", "a query string")]
    [Arguments("unix:///fake.sock#frag-sentinel-4d1c", "a fragment")]
    [Arguments("tcp://someone:sekrit-9f3a@docker.remote:2375", "user information")]
    public async Task InspectAsync_WhenTheConfiguredEndpointCarriesASecret_RefusesItAndNamesOnlyTheComponent(string configured,
        string component)
    {
        // Development Mode renders its endpoint into the message on this page, into the log line and into the pin it
        // would write, so an endpoint holding a secret has to be refused before a client exists rather than after the
        // probe. The query and fragment cases are the ones no other check catches: unix:///fake.sock is a LOCAL
        // socket, so nothing else about it is refusable.
        var (service, _, store) = CreateService(DockerSandboxHardeningTests.Options() with
        {
            DaemonEndpoint = configured
        });

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.NotConfigured, preflight.Status);
        AssertEx.False(preflight.Ready);
        AssertEx.Contains(preflight.Message, component);
        foreach (var secret in Secrets)
        {
            AssertEx.False(preflight.Message.Contains(secret, StringComparison.Ordinal),
                $"The refusal disclosed '{secret}' in the course of declining to use it: {preflight.Message}");
        }

        // Refused rather than pinned: a daemon this node will not talk to must not become the daemon it approved, and
        // the pin is where a secret would have been written to disk.
        AssertEx.Null(await store.ReadAsync());
    }

    private static (IDockerDaemonPreflightService Service, FakeDockerRuntimeClient Client, IDockerDaemonAttestationStore Store) CreateService(ContainerSandboxOptions? options = null,
        IDockerDaemonAttestationStore? attestationStore = null)
    {
        var resolved = options ?? DockerSandboxHardeningTests.Options() with
        {
            DaemonEndpoint = "unix:///fake.sock"
        };
        var endpoint = new DockerDaemonEndpoint
        {
            Uri = new Uri("unix:///fake.sock"),
            Source = DockerDaemonEndpointSource.Configuration
        };
        var client = new FakeDockerRuntimeClient(endpoint,
            new DockerDaemonIdentity
            {
                DaemonId = "daemon-alpha",
                ServerVersion = "99.0.0",
                ApiVersion = "1.99",
                MinimumApiVersion = "1.40",
                OperatingSystem = "linux",
                Endpoint = endpoint,
                IsRootless = false,
                SupportsSeccomp = true
            });
        var store = attestationStore ?? new InMemoryAttestationStore();

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
