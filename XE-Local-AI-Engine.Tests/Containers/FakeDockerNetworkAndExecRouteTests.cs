namespace XE_Local_AI_Engine.Tests.Containers;

using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Testing.FakeDocker;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The production wire client against the fake daemon's network and exec routes.
///     <para>
///         The network half is daemon constraint modelling rather than kernel truth — a name conflict, an ownership
///         comparison, a removal refused while endpoints are attached — which is exactly the class of behaviour an
///         HTTP-level fake can reproduce faithfully. The exec half proves the three-call create/start/inspect dance
///         and Docker's frame demultiplexing; what a real process would have done with the command is not modelled
///         and is not claimed.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class FakeDockerNetworkAndExecRouteTests
{
    private const string Image = "busybox@sha256:0000000000000000000000000000000000000000000000000000000000000001";

    /// <summary>Replaces <c>RealDaemon_CreateNetwork_IsFoundByItsLabelsAndIsIdempotent</c>.</summary>
    [Test]
    public async Task CreateNetwork_IsIdempotentAndIsFoundByItsLabels()
    {
        // Re-entrancy after a crash: the reconciler re-creates a network it may already have created, and the second
        // call must return the same id rather than leave two networks wearing one name.
        await using var box = await FakeDockerRuntimeBox.StartAsync();

        var first = await box.Runtime.CreateNetworkAsync(NetworkSpecification());
        var second = await box.Runtime.CreateNetworkAsync(NetworkSpecification());

        AssertEx.Equal(first, second);

        var found = await box.Runtime.ListNetworksAsync(Labels);
        AssertEx.Equal(expected: 1, found.Count, "The label filter found something other than exactly this network.");
        AssertEx.Equal(first, found[0]);
    }

    /// <summary>Replaces <c>RealDaemon_CreateNetwork_OverAForeignNetworkOfTheSameName_ThrowsContainerPolicyException</c>.</summary>
    [Test]
    public async Task CreateNetwork_OverAForeignNetworkOfTheSameName_ThrowsContainerPolicyException()
    {
        // A name conflict is not proof of ownership. Reusing a network somebody else created would put the
        // application on a bridge with everything else attached to it, which is what makes the instance labels a
        // security input rather than bookkeeping.
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        var foreign = box.State.SeedNetwork("xe-app-net",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner"] = "somebody-else"
            });

        var exception = await AssertEx.ThrowsAsync<ContainerPolicyException>(() => box.Runtime.CreateNetworkAsync(NetworkSpecification()));

        AssertEx.Equal(ContainerPolicyException.ForeignNetworkReason, exception.Reason);
        AssertEx.Contains(exception.Message, "xe-app-net");

        // Refused, not repaired: removing a network this engine does not own is a worse answer than declining.
        AssertEx.Contains(box.State.Networks.Keys, "xe-app-net");
        AssertEx.Equal(foreign.Id, box.State.Networks["xe-app-net"].Id);
    }

    [Test]
    public async Task CreateNetwork_OverANetworkWithADifferentDriver_IsAlsoForeign()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedNetwork("xe-app-net", Labels, driver: "macvlan");

        var exception = await AssertEx.ThrowsAsync<ContainerPolicyException>(() => box.Runtime.CreateNetworkAsync(NetworkSpecification()));

        AssertEx.Equal(ContainerPolicyException.ForeignNetworkReason, exception.Reason);
    }

    [Test]
    public async Task ListNetworks_ExcludesANetworkWhoseLabelsDoNotMatch()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        await box.Runtime.CreateNetworkAsync(NetworkSpecification());
        box.State.SeedNetwork("someone-elses-net",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner"] = "somebody-else"
            });

        var found = await box.Runtime.ListNetworksAsync(Labels);

        AssertEx.Equal(expected: 1, found.Count, "The label filter returned a network this engine does not own.");
    }

    /// <summary>
    ///     Replaces
    ///     <c>RealDaemon_RemoveNetwork_WhileAttached_Fails_ThenSucceedsAfterRemoval_AndAMissingNetworkIsNotAnError</c>.
    /// </summary>
    [Test]
    public async Task RemoveNetwork_WhileAttached_Fails_ThenSucceedsAfterTheContainerIsGone_AndAMissingOneIsNotAnError()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        var networkId = await box.Runtime.CreateNetworkAsync(NetworkSpecification());
        var containerId = await box.Runtime.RunContainerAsync(Specification());

        // The daemon refuses a network that still has endpoints, and it is that refusal that makes teardown
        // ordering observable rather than a silent leak.
        await AssertEx.ThrowsAsync<DockerRuntimeException>(() => box.Runtime.RemoveNetworkAsync(networkId));

        await box.Runtime.RemoveContainerAsync(containerId);
        await box.Runtime.RemoveNetworkAsync(networkId);
        AssertEx.Empty(box.State.Networks, "The network survived a removal that reported success.");

        // A second removal is a 404 the client swallows: already gone is the outcome the caller wanted.
        await box.Runtime.RemoveNetworkAsync(networkId);
        AssertEx.Empty(box.State.Networks);
    }

    /// <summary>
    ///     The wiring half of
    ///     <c>RealDaemon_ProbeWritablePath_IsTrueOnAnEngineCreatedBindMount_AndFalseOnAReadOnlyOneOrAMissingPath</c>,
    ///     which stays in the real-daemon suite because whether a mount is really writable is a kernel claim.
    /// </summary>
    [Test]
    public async Task ProbeWritablePath_IsTrueOnAZeroExitAndFalseOnAnythingElse()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("xe-app-net");
        var containerId = await box.Runtime.RunContainerAsync(Specification());

        // What this proves is the exec create/start/inspect wiring and the exit-code branch, not filesystem truth:
        // whether a real mount is really writable is a kernel claim and stays with the real-daemon suite.
        box.State.ScriptExec(containerId, FakeDockerState.AnyCommand, exitCode: 0);
        AssertEx.True(await box.Runtime.ProbeWritablePathAsync(containerId, "/data"),
            "A write probe whose exec exited zero did not read back as writable.");

        box.State.ScriptExec(containerId, FakeDockerState.AnyCommand, exitCode: 1, standardError: "Read-only file system");
        AssertEx.False(await box.Runtime.ProbeWritablePathAsync(containerId, "/data"),
            "A write probe whose exec exited non-zero read back as writable.");
    }

    [Test]
    public async Task ProbeWritablePath_OnAContainerTheDaemonDoesNotHave_IsFalseRatherThanAThrow()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();

        AssertEx.False(await box.Runtime.ProbeWritablePathAsync("0000000000000000", "/data"),
            "A write probe against an absent container threw instead of answering no.");
    }

    [Test]
    public async Task Execute_ReturnsBothStreamsDemultiplexedAndTheExitCode()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("xe-app-net");
        var containerId = await box.Runtime.RunContainerAsync(Specification());
        box.State.ScriptExec(containerId, "sh -c report", exitCode: 3, standardOutput: "on stdout", standardError: "on stderr");

        IDockerRuntimeClient client = box.Runtime;
        var outcome = await client.ExecuteAsync(containerId,
            new DockerExecutionRequest
            {
                Executable = "sh",
                Arguments = ["-c", "report"],
                MaxCapturedBytes = 4096
            });

        AssertEx.Equal(expected: 3L, outcome.ExitCode);
        AssertEx.Equal("on stdout", outcome.StandardOutput);
        AssertEx.Equal("on stderr", outcome.StandardError);
        AssertEx.False(outcome.StandardOutputTruncated, "A short exec output was reported as truncated.");
    }

    [Test]
    public async Task Execute_BoundsTheCaptureAtTheRequestedCeiling()
    {
        // The other end of this stream is a process inside an image the engine did not build, so the ceiling has to
        // be applied during the read rather than to what was already buffered.
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("xe-app-net");
        var containerId = await box.Runtime.RunContainerAsync(Specification());
        box.State.ScriptExec(containerId, "sh -c flood", exitCode: 0, standardOutput: new string('x', count: 4096));

        IDockerRuntimeClient client = box.Runtime;
        var outcome = await client.ExecuteAsync(containerId,
            new DockerExecutionRequest
            {
                Executable = "sh",
                Arguments = ["-c", "flood"],
                MaxCapturedBytes = 64
            });

        AssertEx.Equal(expected: 64, outcome.StandardOutput.Length);
        AssertEx.True(outcome.StandardOutputTruncated, "An output past the ceiling was not reported as truncated.");
    }

    [Test]
    public async Task Execute_SendsTheCommandAndWorkingDirectoryTheCallerAskedFor()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);
        box.State.SeedNetwork("xe-app-net");
        var containerId = await box.Runtime.RunContainerAsync(Specification());

        IDockerRuntimeClient client = box.Runtime;
        await client.ExecuteAsync(containerId,
            new DockerExecutionRequest
            {
                Executable = "git",
                Arguments = ["status", "--porcelain"],
                WorkingDirectory = "/workspace",
                MaxCapturedBytes = 4096
            });

        var session = AssertEx.NotNull(box.State.ExecSessions.Values.SingleOrDefault(), "The client created something other than one exec.");
        AssertEx.Equal("git status --porcelain", session.CommandLine);
        AssertEx.Equal("/workspace", session.WorkingDirectory);
        AssertEx.Equal(containerId, session.ContainerId);
        AssertEx.True(session.ExitCode is not null, "The exec was never started, so no exit code was recorded.");
        AssertEx.Equal(expected: 0L, session.ExitCode!.Value);
    }

    private static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["owner"] = "xe"
    };

    private static ContainerNetworkSpecification NetworkSpecification()
    {
        return new ContainerNetworkSpecification
        {
            Name = "xe-app-net",
            Labels = Labels,
            Internal = false
        };
    }

    private static ContainerSpecification Specification()
    {
        return new ContainerSpecification
        {
            Image = Image,
            Name = "app-one",
            Labels = Labels,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Mounts = [],
            PublishedPorts = [],
            CapabilitiesToDrop = ["ALL"],
            CapabilitiesToAdd = [],
            SecurityOptions = ["no-new-privileges:true"],
            ReadOnlyRootFilesystem = true,
            NetworkName = "xe-app-net",
            NetworkAliases = ["app"],
            RestartMode = ContainerRestartMode.None,
            MemoryBytes = 0,
            NanoCpus = 0,
            PidsLimit = 256
        };
    }
}
