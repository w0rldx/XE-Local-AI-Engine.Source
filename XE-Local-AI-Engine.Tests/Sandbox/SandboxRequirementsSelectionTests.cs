namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The selection layer of ADR 0007: a workload declares <see cref="SandboxRequirements" />, the selector picks the
///     minimal-satisfying registered backend, fails closed naming the unmet axis when none fits, and records what it
///     did.
///     <para>
///         <b>One property here is a compile-time guarantee and therefore has no test.</b>
///         <see cref="SandboxRequirements.IsolationFloor" /> is a <c>required</c> init property with no default, so a
///         declaration that omits it does not compile — writing
///         <c>new SandboxRequirements { Workload = "x", Toolchain = …, NetworkFloor = …, Persistence = … }</c> is
///         CS9035. That is ADR 0007 Decision 4's "no unisolated fallback" mechanism, and a runtime test cannot assert
///         it: there is no value to pass and no exception to catch. It is stated here so a reader does not conclude the
///         guarantee is untested; it is unfalsifiable at runtime by construction, which is stronger.
///     </para>
/// </summary>
public sealed class SandboxRequirementsSelectionTests
{
    /// <summary>
    ///     Minimal-satisfying, not most-capable-wins. With nothing constraining the candidate set, the least privileged
    ///     backend that can honour the declaration wins — the deterministic fake, which executes nothing at all. This
    ///     is also the old "an unset provider means the fake" rule, now a consequence of the ranking rather than a
    ///     special case of its own.
    /// </summary>
    [Test]
    public async Task ResolveAgent_WithNoConstraint_PicksTheLeastPrivilegedSatisfyingBackend()
    {
        await using var services = BuildServices(agentProvider: null);

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, SandboxProviderSelector.ResolveAgent(services).ProviderName);
    }

    [Test]
    public async Task ResolveAgent_WhenTheConstraintNamesProcess_NarrowsTheCandidateSetToIt()
    {
        await using var services = BuildServices("process");

        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, SandboxProviderSelector.ResolveAgent(services).ProviderName);
    }

    /// <summary>
    ///     The guarantee that used to be an absent <c>implements</c> clause. An operator can point the agent key
    ///     straight at the container backend and still not get one: AgentHome declares
    ///     <see cref="SandboxToolchainSource.HostToolchain" />, no container backend supplies that, and there is no
    ///     downgrade path — the resolution refuses instead.
    /// </summary>
    [Test]
    public async Task ResolveAgent_WhenTheConstraintNamesTheContainerBackend_FailsClosedOnTheToolchainAxis()
    {
        await using var services = BuildServices("docker");

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() =>
            _ = SandboxProviderSelector.ResolveAgent(services));

        AssertEx.Contains(exception.Message, "AgentHome");
        AssertEx.Contains(exception.Message, "toolchain source");
        AssertEx.Contains(exception.Message, "AgentHome:Sandbox:Provider");
    }

    [Test]
    public async Task ResolveWorkSession_WhenTheConstraintNamesTheContainerBackend_FailsClosedOnTheToolchainAxis()
    {
        await using var services = BuildServices("docker");

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() =>
            _ = SandboxProviderSelector.ResolveWorkSession(services));

        AssertEx.Contains(exception.Message, "WorkSession");
        AssertEx.Contains(exception.Message, "toolchain source");
    }

    /// <summary>
    ///     A container-configured node declares an image-backed toolchain without naming a backend anywhere, and gets
    ///     the container backend — which is the whole point of ADR 0007: the permission is bounded by a declared need
    ///     rather than by a feature name. Note the agent key still says <c>process</c> and is NOT inherited here.
    /// </summary>
    [Test]
    public async Task ResolveDevelopment_WhenTheNodeConfiguresAnImage_ResolvesTheContainerBackend()
    {
        await using var services = BuildServices("process", image: PinnedImage);

        AssertEx.Equal(DockerSandboxRuntimeProvider.Name, SandboxProviderSelector.ResolveDevelopment(services).ProviderName);
        // The Development declaration must not disturb what AgentHome and Coder execute on.
        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, SandboxProviderSelector.ResolveAgent(services).ProviderName);
    }

    /// <summary>
    ///     The migration rule ADR 0007 insists on: a set key is never silently reinterpreted. A node that configures an
    ///     image and pins the Development key to a backend that cannot supply one is contradicting itself, and gets a
    ///     startup refusal naming the axis rather than a quiet fall back to the host toolchain it was trying to leave.
    /// </summary>
    [Test]
    public async Task ResolveDevelopment_WhenAnImageIsConfiguredButTheKeyPinsAHostBackend_FailsClosedNamingTheAxis()
    {
        await using var services = BuildServices("process", developmentProvider: "process", image: PinnedImage);

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() =>
            _ = SandboxProviderSelector.ResolveDevelopment(services));

        AssertEx.Contains(exception.Message, "toolchain source (EngineApprovedImage)");
        AssertEx.Contains(exception.Message, "Development:Sandbox:Provider");
        AssertEx.Contains(exception.Message, "Clear that key");
    }

    /// <summary>
    ///     Naming the container backend is itself a declaration of an image-toolchain need — that is what the key
    ///     always meant — so it resolves even on a node that configured no image, exactly as it did before ADR 0007.
    /// </summary>
    [Test]
    public async Task ResolveDevelopment_WhenTheKeyNamesTheContainerBackendWithNoImage_StillResolvesIt()
    {
        await using var services = BuildServices("process", developmentProvider: "docker");

        AssertEx.Equal(DockerSandboxRuntimeProvider.Name, SandboxProviderSelector.ResolveDevelopment(services).ProviderName);
    }

    /// <summary>
    ///     A backend the node never registered is a different diagnosis from one that cannot serve the workload, and
    ///     the message has to say which.
    /// </summary>
    [Test]
    public async Task ResolveDevelopment_WhenTheContainerBackendIsNotRegistered_SaysSo()
    {
        await using var services = BuildServices("process", developmentProvider: "docker", registerContainerBackend: false);

        var exception = AssertEx.Throws<SandboxCapabilityNotSupportedException>(() =>
            _ = SandboxProviderSelector.ResolveDevelopment(services));

        AssertEx.Contains(exception.Message, "docker: not registered");
    }

    [Test]
    public async Task Resolve_WhenTheConstraintNamesAnUnknownBackend_ThrowsNamingItAndTheKey()
    {
        await using var services = BuildServices("does-not-exist");

        var exception = AssertEx.Throws<InvalidOperationException>(() => _ = SandboxProviderSelector.ResolveAgent(services));

        AssertEx.Contains(exception.Message, "does-not-exist");
        AssertEx.Contains(exception.Message, "AgentHome:Sandbox:Provider");
        AssertEx.Contains(exception.Message, "fake, process, docker");
    }

    /// <summary>
    ///     ADR 0007 accepts that a consumer can no longer tell from its own file which backend it got. That trade is
    ///     only worth making if the resolution is recorded, so the log line is a requirement rather than a courtesy —
    ///     declaration, candidates, winner, and rejected candidates with reasons, at Information.
    /// </summary>
    [Test]
    public async Task Resolve_RecordsTheResolutionAtInformation()
    {
        using var recorder = new RecordingLoggerProvider();
        await using var services = BuildServices("process", image: PinnedImage, loggerProvider: recorder);

        _ = SandboxProviderSelector.ResolveDevelopment(services);

        var entry = AssertEx.NotNull(recorder.Entries.SingleOrDefault(candidate => candidate.Level == LogLevel.Information),
            "The selector must record exactly one resolution per role, at Information.");
        AssertEx.Contains(entry.Message, "DevelopmentMode (image toolchain)");
        AssertEx.Contains(entry.Message, "backend 'docker'");
        AssertEx.Contains(entry.Message, "toolchain=EngineApprovedImage");
        AssertEx.Contains(entry.Message, "Constraint: none");
        // The rejected candidates and their reasons are the half that makes the line a diagnosis rather than a claim.
        AssertEx.Contains(entry.Message, "fake: cannot honour toolchain source");
        AssertEx.Contains(entry.Message, "process: cannot honour toolchain source");
    }

    [Test]
    public void FindUnmetAxis_WhenEveryAxisIsMet_ReturnsNull()
    {
        AssertEx.Null(SandboxProviderSelector.FindUnmetAxis(SandboxWorkloads.AgentHome,
            SandboxToolchainSource.HostToolchain,
            () => SandboxProviderCapabilities.None));
    }

    /// <summary>
    ///     The floor is the PROPERTY, so a backend with the host-filesystem boundary clears it even though it does not
    ///     serve <see cref="SandboxIsolationMode.Filesystem" />'s create-request contract. What keeps <c>run_python</c>
    ///     off the container backend is the toolchain axis, not this one — asserted here so a later reader does not
    ///     "restore" the floor check to the narrower mechanism flag and think nothing changed.
    /// </summary>
    [Test]
    public void FindUnmetAxis_WhenOnlyTheBoundaryIsAdvertised_TheIsolationFloorIsStillMet()
    {
        AssertEx.Null(SandboxProviderSelector.FindUnmetAxis(SandboxWorkloads.RunPython,
            SandboxToolchainSource.HostToolchain,
            () => SandboxProviderCapabilities.SupportsHostFilesystemBoundary));

        AssertEx.Equal("toolchain source (HostToolchain)",
            SandboxProviderSelector.FindUnmetAxis(SandboxWorkloads.RunPython,
                SandboxToolchainSource.EngineApprovedImage,
                () => SandboxProviderCapabilities.SupportsHostFilesystemBoundary));
    }

    [Test]
    public void FindUnmetAxis_WhenTheIsolationFloorIsUnmet_NamesIt()
    {
        AssertEx.Equal("isolation floor (Filesystem)",
            SandboxProviderSelector.FindUnmetAxis(SandboxWorkloads.RunPython,
                SandboxToolchainSource.HostToolchain,
                () => SandboxProviderCapabilities.None));
    }

    /// <summary>
    ///     <c>run_python</c> declares both a filesystem floor and no egress, and a backend that has the filesystem
    ///     boundary satisfies both — the isolated chain's own <c>--unshare-net</c> is what enforces the second, so
    ///     gating on the separate egress mechanism would refuse a host that isolates perfectly well. This mirrors the
    ///     reasoning already written into <c>ComputeToolGateway.ExecuteAsync</c>.
    ///     <para>
    ///         Both filesystem flags are supplied because that is what the process backend really advertises: one
    ///         probe result sets them together. The floor is checked against the boundary flag alone.
    ///     </para>
    /// </summary>
    [Test]
    public void FindUnmetAxis_WhenIsolationIsAdvertised_DoesNotAlsoRequireTheSeparateEgressCapability()
    {
        AssertEx.Null(SandboxProviderSelector.FindUnmetAxis(SandboxWorkloads.RunPython,
            SandboxToolchainSource.HostToolchain,
            () => SandboxProviderCapabilities.SupportsFilesystemIsolation | SandboxProviderCapabilities.SupportsHostFilesystemBoundary));
    }

    [Test]
    public void FindUnmetAxis_WhenTheNetworkFloorIsUnmetWithoutAnIsolationFloor_NamesIt()
    {
        var requirements = SandboxWorkloads.AgentHome with { NetworkFloor = SandboxNetworkPolicy.None };

        AssertEx.Equal("network posture (None)",
            SandboxProviderSelector.FindUnmetAxis(requirements, SandboxToolchainSource.HostToolchain, () => SandboxProviderCapabilities.None));
    }

    [Test]
    public void FindUnmetAxis_WhenPersistenceIsUnmet_NamesIt()
    {
        AssertEx.Equal("persistence (PreservedTrustedHostWorkspace)",
            SandboxProviderSelector.FindUnmetAxis(SandboxWorkloads.DevelopmentModeHostToolchain,
                SandboxToolchainSource.HostToolchain,
                () => SandboxProviderCapabilities.None));
    }

    /// <summary>
    ///     The disk ceiling may only TIGHTEN the operator's node-wide number, so a backend that ignores it is no worse
    ///     off than one that honours it. Rejecting a candidate over it would refuse a sandbox for asking to be smaller.
    /// </summary>
    [Test]
    public void FindUnmetAxis_IgnoresTheDiskCeiling()
    {
        var requirements = SandboxWorkloads.AgentHome with { MaxDiskBytes = 1 };

        AssertEx.Null(SandboxProviderSelector.FindUnmetAxis(requirements,
            SandboxToolchainSource.HostToolchain,
            () => SandboxProviderCapabilities.None));
    }

    /// <summary>
    ///     Digest-pinned because <c>ContainerSandboxOptionsValidator</c> rejects a tag-only reference, and the value
    ///     here has to be one an operator could really configure.
    /// </summary>
    private const string PinnedImage =
        "mcr.microsoft.com/dotnet/sdk@sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private static ServiceProvider BuildServices(string? agentProvider,
        string? developmentProvider = null,
        string? image = null,
        bool registerContainerBackend = true,
        ILoggerProvider? loggerProvider = null)
    {
        var configurationValues = new Dictionary<string, string?>();
        if (agentProvider is not null)
        {
            configurationValues["AgentHome:Sandbox:Provider"] = agentProvider;
        }

        if (developmentProvider is not null)
        {
            configurationValues["Development:Sandbox:Provider"] = developmentProvider;
        }

        if (image is not null)
        {
            configurationValues["Development:ContainerSandbox:Image"] = image;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging(builder =>
        {
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        services.AddOptions<SandboxOptions>().Bind(configuration.GetSection(SandboxOptions.SectionName));
        services.AddOptions<DevelopmentSandboxOptions>().Bind(configuration.GetSection(DevelopmentSandboxOptions.SectionName));
        services.AddOptions<LocalContainerOptions>().Bind(configuration.GetSection(LocalContainerOptions.SectionName));
        services.AddOptions<ContainerSandboxOptions>().Bind(configuration.GetSection(ContainerSandboxOptions.SectionName));
        services.AddSingleton(Substitute.For<IDockerRuntimeClientFactory>());
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(Path.Combine(Path.GetTempPath(), "xe-selection-tests")));
        services.AddSingleton<FakeSandboxRuntimeProvider>();
        services.AddSingleton<ProcessSandboxRuntimeProvider>();
        if (registerContainerBackend)
        {
            // Registered exactly as AddNodeContainerSandbox does — a module of its own, so a node that never added it
            // has no container backend to select at all.
            services.AddSingleton<DockerSandboxRuntimeProvider>();
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     The selector logs through <see cref="ILoggerFactory.CreateLogger(string)" /> with its own type name as the
    ///     category, so a <see cref="RecordingLogger{T}" /> cannot see it; this captures every category instead.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<RecordingLogger<SandboxRequirementsSelectionTests>.Entry> _entries = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<RecordingLogger<SandboxRequirementsSelectionTests>.Entry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(this);
        }

        public void Dispose()
        {
        }

        private void Add(RecordingLogger<SandboxRequirementsSelectionTests>.Entry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        private sealed class CapturingLogger(RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) =>
                true;

            public void Log<TState>(LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Add(new RecordingLogger<SandboxRequirementsSelectionTests>.Entry(logLevel, formatter(state, exception), exception));
            }
        }
    }
}
