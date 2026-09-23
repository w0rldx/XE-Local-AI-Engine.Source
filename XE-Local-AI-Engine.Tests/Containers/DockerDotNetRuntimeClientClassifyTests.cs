namespace XE_Local_AI_Engine.Tests.Containers;

using System.Net.Sockets;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     How the production client names a daemon that is not there, tested without one: the transport-failure
///     classification. The named-pipe preflight lives in <see cref="DockerNamedPipePreflightTests" />.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DockerDotNetRuntimeClientClassifyTests
{
    [Test]
    public async Task ATimeout_DirectOrWrapped_IsDaemonUnreachable()
    {
        // The npipe transport throws TimeoutException when no listener takes its connect; that is "start Docker",
        // not the catch-all "could not be used" the tester saw.
        await using var client = ClientFor("unix:///xe-classify-tests.sock");
        Exception[] failures =
        [
            new System.TimeoutException("The operation has timed out."),
            new InvalidOperationException("transport failed", new System.TimeoutException("The operation has timed out.")),
            new AggregateException(new System.TimeoutException("The operation has timed out."))
        ];

        foreach (var failure in failures)
        {
            var classified = client.Classify(failure);

            AssertEx.Equal(DockerDaemonPreflightStatus.DaemonUnreachable, classified.Status, failure.GetType().Name);
            AssertEx.False(classified.Message.Contains("could not be used", StringComparison.Ordinal), classified.Message);
            AssertEx.Contains(classified.Message, "did not answer in time");
        }
    }

    [Test]
    public async Task AnUnrecognisedFailure_IsStillProbeFailed()
    {
        // The control: the timeout arm must not have swallowed the catch-all.
        await using var client = ClientFor("unix:///xe-classify-tests.sock");

        var classified = client.Classify(new InvalidOperationException("something else"));

        AssertEx.Equal(DockerDaemonPreflightStatus.ProbeFailed, classified.Status);
    }

    [Test]
    public async Task ASocketAccessDenied_StaysPermissionDenied()
    {
        await using var client = ClientFor("unix:///xe-classify-tests.sock");

        var classified = client.Classify(new HttpRequestException("denied", new SocketException((int)SocketError.AccessDenied)));

        AssertEx.Equal(DockerDaemonPreflightStatus.PermissionDenied, classified.Status);
    }

    private static DockerDotNetRuntimeClient ClientFor(string endpoint)
    {
        // DockerClientBuilder opens nothing at construction; only ProbeAsync touches the transport.
        return new DockerDotNetRuntimeClient(new DockerDaemonEndpoint { Uri = new Uri(endpoint), Source = DockerDaemonEndpointSource.Configuration },
            TimeSpan.FromSeconds(1),
            TimeProvider.System);
    }
}
