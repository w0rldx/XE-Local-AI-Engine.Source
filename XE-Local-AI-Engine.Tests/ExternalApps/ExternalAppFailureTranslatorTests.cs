namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Net;
using System.Net.Sockets;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One case per row of the translation table. It keys on the phase because daemon prose changes between Docker
///     releases, and a translator matching on message text degrades to "Unknown" after an upgrade nobody connected
///     to it.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppFailureTranslatorTests
{
    private const string Secret = "s3cr3t-admin-password";

    /// <summary>A policy refusal is a policy violation wherever it happened, phase included.</summary>
    [Test]
    [Arguments(ExternalAppFailurePhase.Pull)]
    [Arguments(ExternalAppFailurePhase.Create)]
    [Arguments(ExternalAppFailurePhase.Network)]
    public void Translate_AContainerPolicyException_IsAPolicyViolationInEveryPhase(ExternalAppFailurePhase phase)
    {
        var failure = ExternalAppFailureTranslator.Translate(phase,
            new ContainerPolicyException(ContainerPolicyException.ForeignNetworkReason, "a network with this name is not ours"));

        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, failure.Category);
    }

    [Test]
    public void Translate_ADaemonExceptionWithAStatus_IsRuntimeUnavailable()
    {
        var failure = ExternalAppFailureTranslator.Translate(ExternalAppFailurePhase.Pull,
            new DockerRuntimeException(DockerDaemonPreflightStatus.PermissionDenied, "the daemon refused"));

        AssertEx.Equal(ExternalAppFailureCategory.RuntimeUnavailable, failure.Category);
    }

    /// <summary>
    ///     ProbeFailed is the type's own default, i.e. "no status was stated". It must not be read as the daemon
    ///     saying it cannot serve this engine, or every ordinary pull failure would report the runtime as broken.
    /// </summary>
    [Test]
    public void Translate_ADaemonExceptionWithoutAStatus_FallsThroughToThePhase()
    {
        var failure = ExternalAppFailureTranslator.Translate(ExternalAppFailurePhase.Pull,
            new DockerRuntimeException("the registry timed out"));

        AssertEx.Equal(ExternalAppFailureCategory.ImagePullFailed, failure.Category);
    }

    [Test]
    public void Translate_ARuntimeUnavailableException_IsRuntimeUnavailable()
    {
        var resolution = new ContainerRuntimeResolution
        {
            Provider = "docker",
            Status = ContainerRuntimeStatus.DaemonUnreachable,
            Capabilities = ContainerRuntimeCapabilities.None,
            Message = "no daemon answered",
            Daemon = new ContainerDaemonSummary
            {
                Endpoint = "unix:///var/run/docker.sock",
                EndpointSource = DockerDaemonEndpointSource.DefaultUnixSocket
            }
        };

        var failure = ExternalAppFailureTranslator.Translate(ExternalAppFailurePhase.Resolution, new ContainerRuntimeUnavailableException(resolution));

        AssertEx.Equal(ExternalAppFailureCategory.RuntimeUnavailable, failure.Category);
    }

    [Test]
    [Arguments(ExternalAppFailurePhase.Resolution, ExternalAppFailureCategory.RuntimeIncompatible)]
    [Arguments(ExternalAppFailurePhase.Plan, ExternalAppFailureCategory.ConfigurationMissing)]
    [Arguments(ExternalAppFailurePhase.Storage, ExternalAppFailureCategory.StorageError)]
    [Arguments(ExternalAppFailurePhase.Pull, ExternalAppFailureCategory.ImagePullFailed)]
    [Arguments(ExternalAppFailurePhase.Verify, ExternalAppFailureCategory.PolicyViolation)]
    [Arguments(ExternalAppFailurePhase.Wait, ExternalAppFailureCategory.HealthCheckFailed)]
    [Arguments(ExternalAppFailurePhase.Network, ExternalAppFailureCategory.Unknown)]
    public void Translate_KeysOnThePhase(ExternalAppFailurePhase phase, ExternalAppFailureCategory expected)
    {
        var failure = ExternalAppFailureTranslator.Translate(phase, new InvalidOperationException("something the daemon said"));

        AssertEx.Equal(expected, failure.Category);
    }

    /// <summary>
    ///     The port discrimination is a re-probe rather than a message match: deterministic, and true of the box
    ///     rather than of whatever text the daemon happened to produce.
    /// </summary>
    [Test]
    public void Translate_WhenAPlannedHostPortIsNoLongerBindable_IsPortUnavailable()
    {
        using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, port: 0));
        occupied.Listen(backlog: 1);
        var taken = ((IPEndPoint)occupied.LocalEndPoint!).Port;

        var failure = ExternalAppFailureTranslator.Translate(ExternalAppFailurePhase.Create, new InvalidOperationException("bind failed"), [taken]);

        AssertEx.Equal(ExternalAppFailureCategory.PortUnavailable, failure.Category);
    }

    [Test]
    public void Translate_WhenThePlannedHostPortIsStillFree_IsNotPortUnavailable()
    {
        int free;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port: 0));
            free = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        var failure = ExternalAppFailureTranslator.Translate(ExternalAppFailurePhase.Create, new InvalidOperationException("create failed"), [free]);

        AssertEx.NotEqual(ExternalAppFailureCategory.PortUnavailable, failure.Category);
    }

    [Test]
    public void ForMissingCapabilities_NamesThemAndIsRuntimeIncompatible()
    {
        var failure = ExternalAppFailureTranslator.ForMissingCapabilities(["gpuDevices"]);

        AssertEx.Equal(ExternalAppFailureCategory.RuntimeIncompatible, failure.Category);
        AssertEx.Contains(failure.Summary, "gpuDevices");
    }

    [Test]
    public void ForGpuRequired_IsGpuNotSupported()
    {
        AssertEx.Equal(ExternalAppFailureCategory.GpuNotSupported, ExternalAppFailureTranslator.ForGpuRequired().Category);
    }

    /// <summary>
    ///     The count and the service, and nothing the violations said. A violation is composed against what the daemon
    ///     reported and several of them name a host path; this summary is persisted and rendered in a browser.
    /// </summary>
    [Test]
    public void ForViolations_NamesTheCountAndQuotesNoViolation()
    {
        var failure = ExternalAppFailureTranslator.ForViolations("app",
        [
            "the mount at '/data' is backed by '/home/someone/secrets' rather than by this instance's own directory (rootless daemon)",
            "the container has a device"
        ]);

        AssertEx.Equal(ExternalAppFailureCategory.PolicyViolation, failure.Category);
        AssertEx.Contains(failure.Summary, "2 policy check");
        AssertEx.Contains(failure.Summary, "app");
        AssertEx.False(failure.Summary.Contains("/home/someone/secrets", StringComparison.Ordinal),
            "A violation can name a host path, and this summary reaches a browser.");
        AssertEx.False(failure.Summary.Contains("device", StringComparison.Ordinal), "No violation is quoted.");
    }

    /// <summary>
    ///     The summary is written to a database column and rendered in a browser. A raw exception message can carry
    ///     the environment it failed on, which is where the application's password lives.
    /// </summary>
    [Test]
    public void Translate_NeverRepeatsTheExceptionMessage()
    {
        var failure = ExternalAppFailureTranslator.Translate(ExternalAppFailurePhase.Start,
            new InvalidOperationException($"container refused: ODYSSEUS_ADMIN_PASSWORD={Secret} at /home/user/data"));

        AssertEx.False(failure.Summary.Contains(Secret, StringComparison.Ordinal), "A secret must never reach the summary.");
        AssertEx.False(failure.Summary.Contains("/home/user", StringComparison.Ordinal), "A host path must never reach the summary.");
        AssertEx.NotEmpty(failure.Summary);
    }
}
