namespace XE_Local_AI_Engine.Tests.Containers;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Testing.FakeDocker;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Which <see cref="IContainerRuntime" /> a contract case runs against.</summary>
public enum ContainerRuntimeUnderTest
{
    /// <summary>The lying fake every later slice's tests stand on.</summary>
    Fake,

    /// <summary>The production Docker client, built against an endpoint that does not exist.</summary>
    DockerClient,

    /// <summary>
    ///     The production Docker client against a <c>FakeDockerServer</c> that answers. Used only by the post-guard
    ///     cases: a guard that fires before the first wire call needs no daemon behind it, and starting one to prove
    ///     otherwise would be pure overhead.
    /// </summary>
    FakeServer
}

/// <summary>
///     One suite, run once per <see cref="IContainerRuntime" /> implementation, over the refusals both must make.
///     <para>
///         Before this class the fake was pinned to itself and the production client's copies of the same guards were
///         asserted separately, by hand, in <c>ContainerRuntimeWireMappingTests</c>. Two drifts survived that
///         arrangement long enough to be found in review, so the guards are asserted here once against both
///         implementations instead: a guard added, reworded or dropped on one side and not the other fails for
///         exactly that implementation.
///     </para>
///     <para>
///         The suite is in two halves. The pre-wire half is every refusal that happens BEFORE the first wire call,
///         which is what lets the production client run without a daemon — <c>Fake</c> and <c>DockerClient</c> are
///         built against the same non-existent socket, and a guard that started talking to it would fail here with a
///         transport error rather than pass.
///     </para>
///     <para>
///         The post-guard half is the refusals the daemon itself makes on state it holds — an image that was never
///         pulled, a network that was never created, a name already taken. Those used to be uncontracted, asserted
///         only against the in-memory fake's own promise, because the production side had no daemon to answer them.
///         It has one now: the <c>FakeServer</c> arm puts a <c>FakeDockerServer</c> behind the real client, so the
///         fake's lies and a daemon's 404s and 409s are pinned to each other. <c>DockerClient</c> takes no rows in
///         that half — a socket that does not exist cannot answer — and <c>FakeServer</c> takes none in the pre-wire
///         half, where starting a server to test a guard that fires before any HTTP call is pure overhead.
///     </para>
/// </summary>
public sealed class ContainerRuntimeContractTests
{
    private const string Digest = "@sha256:0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>A bare image id: what a daemon reports for an image its store recorded no <c>RepoDigests</c> for.</summary>
    private const string BareImageId = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
    private const string Image = "ghcr.io/example/app" + Digest;
    private const string NetworkName = "xe-app-instance-1-net";

    /// <summary>
    ///     A socket that does not exist, shared by both implementations. Constructing a client opens nothing, so the
    ///     endpoint is inert until a member touches the wire — and no member asserted here does.
    /// </summary>
    private static readonly Uri UnreachableEndpoint = new("unix:///xe-container-runtime-contract-tests.sock");

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake, "0.0.0.0")]
    [Arguments(ContainerRuntimeUnderTest.Fake, "")]
    [Arguments(ContainerRuntimeUnderTest.Fake, "::")]
    [Arguments(ContainerRuntimeUnderTest.Fake, "192.168.1.10")]
    [Arguments(ContainerRuntimeUnderTest.Fake, "localhost")]
    [Arguments(ContainerRuntimeUnderTest.Fake, "::1")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "0.0.0.0")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "::")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "192.168.1.10")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "localhost")]
    // Refused although it IS loopback: everything on this node that reaches a published port — the engine, the
    // operator's browser — is handed a 127.0.0.1 address, so a binding on the IPv6 loopback reads back as published
    // and answers nobody.
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "::1")]
    public async Task RunContainer_WithANonLoopbackHostIp_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation,
        string hostIp)
    {
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() => client.RunContainerAsync(Specification() with
        {
            PublishedPorts =
            [
                new ContainerPortPublication
                {
                    ContainerPort = 8080,
                    HostIp = hostIp,
                    HostPort = 30080
                }
            ]
        }));

        AssertEx.Equal("specification", failure.ParamName);
        AssertEx.Contains(failure.Message, "127.0.0.1");
        // An empty host IP renders as a binding Docker resolves to 0.0.0.0, so there must be no window in which
        // such a container exists for a later read-back to catch.
        AssertNothingWasCreated(client);
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task RunContainer_WithATagRatherThanADigest_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() => client.RunContainerAsync(Specification() with
        {
            Image = "ghcr.io/example/app:1.2.3"
        }));

        AssertEx.Equal("specification", failure.ParamName);
        AssertEx.Contains(failure.Message, "not digest-pinned");
        AssertNothingWasCreated(client);
    }

    /// <summary>
    ///     A bare image id names its bytes as surely as a digest-pinned reference does, so the guard lets it
    ///     through. It is the only reference a daemon has for an image built locally on a store that records no
    ///     <c>RepoDigests</c>, and refusing it cost <see cref="VolumeDeclaringImageFixture" /> its assertion there.
    /// </summary>
    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task RunContainer_WithABareImageId_IsNotRefusedByTheDigestGuard(ContainerRuntimeUnderTest implementation)
    {
        await using var client = Create(implementation);

        // Past the guard is the assertion, and the exception TYPE carries it: what stops the call is the daemon,
        // not this layer. The fake 404s an image it was never seeded; the production client cannot reach its
        // socket. The guard firing would be an ArgumentException, which this would report as the wrong type.
        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => client.RunContainerAsync(Specification() with
        {
            Image = BareImageId
        }));

        AssertEx.NotEmpty(failure.Message);
    }

    /// <summary>
    ///     The near-misses of both accepted forms. Each is one edit away from a reference that names bytes, and a
    ///     <c>Contains</c>-shaped check or an unanchored regex would let at least one of them through.
    /// </summary>
    [Test]
    // One hex digit short of an id. The length is the whole check.
    [Arguments(ContainerRuntimeUnderTest.Fake, "sha256:000000000000000000000000000000000000000000000000000000000000000")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "sha256:000000000000000000000000000000000000000000000000000000000000000")]
    // Uppercase hex. A daemon writes image ids in lowercase, and accepting both spellings would make one image
    // answer to two references.
    [Arguments(ContainerRuntimeUnderTest.Fake, "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    // 64 hex with no algorithm prefix names nothing a daemon resolves.
    [Arguments(ContainerRuntimeUnderTest.Fake, "0000000000000000000000000000000000000000000000000000000000000000")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "0000000000000000000000000000000000000000000000000000000000000000")]
    // The digest half's own near-misses. A Contains("@sha256:") test admits every one of the first three, and each
    // names no bytes at all: the separator is present and the digest behind it is absent, truncated or not hex.
    [Arguments(ContainerRuntimeUnderTest.Fake, "ghcr.io/example/app@sha256:")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "ghcr.io/example/app@sha256:")]
    [Arguments(ContainerRuntimeUnderTest.Fake, "ghcr.io/example/app@sha256:zz")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "ghcr.io/example/app@sha256:zz")]
    [Arguments(ContainerRuntimeUnderTest.Fake, "ghcr.io/example/app@sha256:000000000000000000000000000000000000000000000000000000000000000")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "ghcr.io/example/app@sha256:000000000000000000000000000000000000000000000000000000000000000")]
    // A digest with no repository in front of it. The daemon resolves a name@sha256: reference through the NAME,
    // so a reference that is only the separator and the digest names nothing.
    [Arguments(ContainerRuntimeUnderTest.Fake, "@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    // Uppercase, on the digest half too: one image must not answer to two spellings.
    [Arguments(ContainerRuntimeUnderTest.Fake, "ghcr.io/example/app@sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [Arguments(ContainerRuntimeUnderTest.DockerClient, "ghcr.io/example/app@sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task RunContainer_WithAMalformedImageId_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation,
        string image)
    {
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() => client.RunContainerAsync(Specification() with
        {
            Image = image
        }));

        AssertEx.Equal("specification", failure.ParamName);
        AssertEx.Contains(failure.Message, "not digest-pinned");
        AssertNothingWasCreated(client);
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task RunContainer_WithABlankUser_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        // `required` means "assigned", not "non-empty": "" and null would otherwise be the same instruction written
        // two ways, and only one of them says what it means.
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() => client.RunContainerAsync(Specification() with
        {
            User = "   "
        }));

        AssertEx.Equal("specification", failure.ParamName);
        AssertEx.Contains(failure.Message, "blank container user is refused");
        AssertNothingWasCreated(client);
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task RunContainer_PublishingOneContainerPortTwice_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        // Container port plus protocol keys both wire dictionaries, so a duplicate would surface as the BCL's own
        // duplicate-key ArgumentException naming neither the port nor the caller. Both layers say which publication.
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() => client.RunContainerAsync(Specification() with
        {
            PublishedPorts =
            [
                new ContainerPortPublication
                {
                    ContainerPort = 8080,
                    HostIp = "127.0.0.1",
                    HostPort = 30080
                },
                new ContainerPortPublication
                {
                    ContainerPort = 8080,
                    HostIp = "127.0.0.1",
                    HostPort = 30081
                }
            ]
        }));

        AssertEx.Equal("specification", failure.ParamName);
        AssertEx.Contains(failure.Message, "8080/tcp");
        AssertEx.Contains(failure.Message, "published more than once");
        AssertNothingWasCreated(client);
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task PullImage_WithoutADigest_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() =>
            client.PullImageAsync("ghcr.io/example/app:latest", progress: null));

        AssertEx.Equal("imageReference", failure.ParamName);
        AssertEx.Contains(failure.Message, "not digest-pinned");
        AssertNothingWasCreated(client);
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task CreateNetwork_WithoutAName_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() => client.CreateNetworkAsync(new ContainerNetworkSpecification
        {
            Name = "   ",
            Labels = Labels(),
            Internal = false
        }));

        AssertEx.Equal("specification", failure.ParamName);
        AssertEx.Contains(failure.Message, "must carry a name");
        AssertNothingWasCreated(client);
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task CreateNetwork_WithNoLabels_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        // The labels ARE the ownership proof a name conflict is reused on. With none of them there is nothing to
        // compare and a foreign bridge holding the name would pass, so the refusal is on the specification itself.
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() => client.CreateNetworkAsync(new ContainerNetworkSpecification
        {
            Name = NetworkName,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Internal = false
        }));

        AssertEx.Equal("specification", failure.ParamName);
        AssertEx.Contains(failure.Message, "ownership label");
        AssertNothingWasCreated(client);
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task ListNetworks_WithoutALabelFilter_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        // An unfiltered list drives a removal, so the empty filter that would match every network on the daemon is
        // refused rather than sent.
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() =>
            client.ListNetworksAsync(new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Equal("labels", failure.ParamName);
        AssertEx.Contains(failure.Message, "without a label filter is refused");
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task ListContainersDetailed_WithoutALabelFilter_IsRefusedBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        await using var client = Create(implementation);

        var failure = await AssertEx.ThrowsAsync<ArgumentException>(() =>
            client.ListContainersDetailedAsync(new Dictionary<string, string>(StringComparer.Ordinal)));

        AssertEx.Equal("labels", failure.ParamName);
        AssertEx.Contains(failure.Message, "without a label filter is refused");
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task EveryIdentifierTakingMember_RefusesABlankIdentifierBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        // A blank id is not a request the daemon can answer, and on the production side it would become a URL with an
        // empty path segment — a call against the collection rather than the item. Refused by every member instead.
        await using var client = Create(implementation);

        await RefusesAsync<ArgumentException>("containerId", () => client.InspectAsync("  "));
        await RefusesAsync<ArgumentException>("containerId", () => client.StopContainerAsync("  ", TimeSpan.FromSeconds(5)));
        await RefusesAsync<ArgumentException>("imageReference", () => client.ImageExistsAsync("  "));
        await RefusesAsync<ArgumentException>("imageReference", () => client.PullImageAsync("  ", progress: null));
        await RefusesAsync<ArgumentException>("networkNameOrId", () => client.RemoveNetworkAsync("  "));
        await RefusesAsync<ArgumentException>("containerId", () => client.ReadLogsAsync("  ", new ContainerLogRequest { TailLines = 10 }));
        await RefusesAsync<ArgumentException>("containerId", () => client.ProbeWritablePathAsync("  ", "/data"));
        await RefusesAsync<ArgumentException>("containerPath", () => client.ProbeWritablePathAsync("container-1", "  "));
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.DockerClient)]
    public async Task EveryReferenceTakingMember_RefusesNullBeforeAnyWireCall(ContainerRuntimeUnderTest implementation)
    {
        await using var client = Create(implementation);

        await RefusesAsync<ArgumentNullException>("specification", () => client.RunContainerAsync(specification: null!));
        await RefusesAsync<ArgumentNullException>("specification", () => client.CreateNetworkAsync(specification: null!));
        await RefusesAsync<ArgumentNullException>("labels", () => client.ListNetworksAsync(labels: null!));
        await RefusesAsync<ArgumentNullException>("labels", () => client.ListContainersDetailedAsync(labels: null!));
        await RefusesAsync<ArgumentNullException>("request", () => client.ReadLogsAsync("container-1", request: null!));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Post-guard cases. Everything above refuses before the first wire call and needs no daemon; everything below
    // needs one behind the guard, which is what the FakeServer arm supplies. These are the promises the in-memory
    // fake makes about daemon-held state — an image that was never pulled, a network that was never created, a name
    // already taken — asserted against the production client too, so the fake's lies and the daemon's answers cannot
    // drift apart unnoticed. Only the shared half is asserted here: the fake names the offending image or network in
    // its message and the production client names the status code, so the status word is what both promise.
    // ---------------------------------------------------------------------------------------------------------

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.FakeServer)]
    public async Task RunContainer_WithAnImageTheDaemonDoesNotHave_IsRefusedAsNotFound(ContainerRuntimeUnderTest implementation)
    {
        await using var box = await PostGuardBox.CreateAsync(implementation);
        box.SeedNetwork();

        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => box.Runtime.RunContainerAsync(Specification()));

        AssertEx.Contains(failure.Message, "NotFound");
        AssertEx.Equal(expected: 0, box.CreatedContainerCount, "A refused create left a container behind.");
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.FakeServer)]
    public async Task RunContainer_OnANetworkThatWasNeverCreated_IsRefusedAsNotFound(ContainerRuntimeUnderTest implementation)
    {
        await using var box = await PostGuardBox.CreateAsync(implementation);
        box.SeedImage();

        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => box.Runtime.RunContainerAsync(Specification()));

        AssertEx.Contains(failure.Message, "NotFound");
        AssertEx.Equal(expected: 0, box.CreatedContainerCount);
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.FakeServer)]
    public async Task RunContainer_UnderANameAlreadyTaken_IsRefusedAsAConflict(ContainerRuntimeUnderTest implementation)
    {
        await using var box = await PostGuardBox.CreateAsync(implementation);
        box.SeedImage();
        box.SeedNetwork();

        var first = await box.Runtime.RunContainerAsync(Specification());

        var failure = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => box.Runtime.RunContainerAsync(Specification()));
        AssertEx.Contains(failure.Message, "Conflict");
        AssertEx.Equal(expected: 1, box.CreatedContainerCount);

        // The name is free again once the container is gone, which is what lets a rebuild recreate it.
        await box.Runtime.RemoveContainerAsync(first);
        AssertEx.NotNullOrEmpty(await box.Runtime.RunContainerAsync(Specification()));
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.FakeServer)]
    public async Task StopContainer_OnAnAlreadyStoppedContainer_ReturnsFalse(ContainerRuntimeUnderTest implementation)
    {
        await using var box = await PostGuardBox.CreateAsync(implementation);
        box.SeedImage();
        box.SeedNetwork();

        var containerId = await box.Runtime.RunContainerAsync(Specification());
        await box.Runtime.StartContainerAsync(containerId);

        AssertEx.True(await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(5)),
            "The first stop of a running container reported that it was already stopped.");
        // False is "it was already stopped", an answer and not an error: a teardown must run twice without the
        // second run looking like a failure.
        AssertEx.False(await box.Runtime.StopContainerAsync(containerId, TimeSpan.FromSeconds(5)));
    }

    [Test]
    [Arguments(ContainerRuntimeUnderTest.Fake)]
    [Arguments(ContainerRuntimeUnderTest.FakeServer)]
    public async Task RemoveNetwork_OnAMissingNetwork_IsNotAnError(ContainerRuntimeUnderTest implementation)
    {
        await using var box = await PostGuardBox.CreateAsync(implementation);

        await box.Runtime.RemoveNetworkAsync("a-network-that-never-existed");

        // Already gone is the state the caller wanted. The assertion is that the call returned at all, so the next
        // line proves the runtime is still usable rather than left in a faulted state by a swallowed 404.
        AssertEx.Empty(await box.Runtime.ListNetworksAsync(Labels()));
    }

    /// <summary>
    ///     The refusal, pinned by the parameter it names as well as by its type. Type alone passes on a wrong
    ///     reason: an <see cref="ArgumentException" /> the BCL or Docker.DotNet raised while building a URI out of a
    ///     blank identifier is not this layer's guard, and reads exactly the same from the outside.
    /// </summary>
    private static async Task RefusesAsync<TException>(string parameterName, Func<Task> call)
        where TException : ArgumentException
    {
        var failure = await AssertEx.ThrowsAsync<TException>(call);

        AssertEx.Equal(parameterName, failure.ParamName);
    }

    /// <summary>
    ///     Refused BEFORE anything was created, not merely refused. Only the fake records what it was asked to
    ///     create, so only the fake can be asked; on the production client the same property is carried by the
    ///     endpoint that does not exist, where a guard that reached the wire fails with a transport error instead of
    ///     passing. A guard that created a container and then refused would leave one behind on both.
    /// </summary>
    private static void AssertNothingWasCreated(IContainerRuntime client)
    {
        if (client is not FakeDockerRuntimeClient fake)
        {
            return;
        }

        AssertEx.Empty(fake.CreatedContainerIds);
        AssertEx.Empty(fake.PulledImages);
        AssertEx.Empty(fake.CreatedNetworks);
    }

    /// <summary>
    ///     Both implementations against the same endpoint that does not exist. The fake ignores it; the production
    ///     client would fail on it, which is what makes "before any wire call" an assertion rather than a claim.
    /// </summary>
    private static IContainerRuntime Create(ContainerRuntimeUnderTest implementation)
    {
        var endpoint = new DockerDaemonEndpoint(UnreachableEndpoint, DockerDaemonEndpointSource.Configuration);

        return implementation switch
        {
            ContainerRuntimeUnderTest.Fake => new FakeDockerRuntimeClient(endpoint),
            ContainerRuntimeUnderTest.DockerClient => new DockerDotNetRuntimeClient(endpoint, TimeSpan.FromSeconds(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(implementation), implementation, "Unknown container runtime implementation.")
        };
    }

    /// <summary>
    ///     A runtime with a daemon behind it, plus the two seeding calls the post-guard cases need, over both
    ///     implementations. Seeding is the one thing the two do differently — the in-memory fake is told directly, the
    ///     production client's daemon is told through the fake server's state — and everything else is the same calls
    ///     in the same order.
    /// </summary>
    private sealed class PostGuardBox : IAsyncDisposable
    {
        private readonly FakeDockerRuntimeClient? _inMemory;
        private readonly FakeDockerRuntimeBox? _server;

        private PostGuardBox(FakeDockerRuntimeClient? inMemory, FakeDockerRuntimeBox? server)
        {
            _inMemory = inMemory;
            _server = server;
        }

        public IContainerRuntime Runtime => (IContainerRuntime?)_inMemory ?? _server!.Runtime;

        /// <summary>How many containers the daemon holds, so "refused before anything was created" is assertable.</summary>
        public int CreatedContainerCount => _inMemory?.CreatedContainerIds.Count ?? _server!.State.Containers.Count;

        [SuppressMessage("Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "Ownership transfers to the returned box, whose DisposeAsync releases both halves and which every caller holds with `await using`. The catch below covers the window before that transfer.")]
        public static async Task<PostGuardBox> CreateAsync(ContainerRuntimeUnderTest implementation)
        {
            FakeDockerRuntimeClient? inMemory = null;
            FakeDockerRuntimeBox? server = null;

            try
            {
                switch (implementation)
                {
                    case ContainerRuntimeUnderTest.Fake:
                        inMemory = new FakeDockerRuntimeClient(new DockerDaemonEndpoint(UnreachableEndpoint,
                            DockerDaemonEndpointSource.Configuration));
                        break;
                    case ContainerRuntimeUnderTest.FakeServer:
                        server = await FakeDockerRuntimeBox.StartAsync();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(implementation),
                            implementation,
                            "Only the implementations that can answer a wire call belong in a post-guard case.");
                }

                return new PostGuardBox(inMemory, server);
            }
            catch
            {
                // Ownership has not transferred to the box yet, so whichever half was built is this method's to
                // release.
                if (inMemory is not null)
                {
                    await inMemory.DisposeAsync();
                }

                if (server is not null)
                {
                    await server.DisposeAsync();
                }

                throw;
            }
        }

        public void SeedImage()
        {
            _inMemory?.SeedExistingImage(Image);
            _server?.State.SeedImage(Image);
        }

        public void SeedNetwork()
        {
            _inMemory?.SeedExistingNetwork(new ContainerNetworkSpecification
            {
                Name = NetworkName,
                Labels = Labels(),
                Internal = false
            });
            _server?.State.SeedNetwork(NetworkName, Labels());
        }

        public async ValueTask DisposeAsync()
        {
            if (_inMemory is not null)
            {
                await _inMemory.DisposeAsync();
            }

            if (_server is not null)
            {
                await _server.DisposeAsync();
            }
        }
    }

    private static Dictionary<string, string> Labels()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["xe.instance"] = "instance-1",
            ["xe.install"] = "install-1"
        };
    }

    private static ContainerSpecification Specification()
    {
        return new ContainerSpecification
        {
            Image = Image,
            Name = "xe-app-instance-1-odysseus",
            Labels = Labels(),
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Mounts = [],
            PublishedPorts = [],
            CapabilitiesToDrop = ["ALL"],
            CapabilitiesToAdd = [],
            SecurityOptions = ["no-new-privileges:true"],
            ReadOnlyRootFilesystem = false,
            NetworkName = NetworkName,
            NetworkAliases = ["odysseus"],
            RestartMode = ContainerRestartMode.UnlessStopped,
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = 512
        };
    }
}
