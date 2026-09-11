namespace XE_Local_AI_Engine.Tests.Containers;

using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Which <see cref="IContainerRuntime" /> a contract case runs against.</summary>
public enum ContainerRuntimeUnderTest
{
    /// <summary>The lying fake every later slice's tests stand on.</summary>
    Fake,

    /// <summary>The production Docker client, built against an endpoint that does not exist.</summary>
    DockerClient
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
///         Every case here is a refusal that happens BEFORE the first wire call, which is what lets the production
///         client run without a daemon — both implementations are built against the same non-existent socket, and a
///         guard that started talking to it would fail here with a transport error rather than pass. Refusals the
///         daemon itself makes on state it holds — an image that was never pulled, a network that was never created,
///         a name already taken — are not contract cases: on the production side they are 404s and 409s off the wire.
///         They stay in <c>ContainerRuntimeFakeContractTests</c> (the fake's own promise) and in
///         <c>ContainerRuntimeRealDaemonTests</c> (the daemon's).
///     </para>
/// </summary>
public sealed class ContainerRuntimeContractTests
{
    private const string Digest = "@sha256:0000000000000000000000000000000000000000000000000000000000000000";
    private const string Image = "ghcr.io/example/app" + Digest;

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
            Name = "xe-app-instance-1-net",
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
            NetworkName = "xe-app-instance-1-net",
            NetworkAliases = ["odysseus"],
            RestartMode = ContainerRestartMode.UnlessStopped,
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = 512
        };
    }
}
