namespace XE_Local_AI_Engine.Tests.Containers;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Testing.FakeDocker;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The production wire client against the fake daemon's identity and image routes.
///     <para>
///         These run the real <c>DockerDotNetRuntimeClient</c> over a real socket, so what they prove is the round
///         trip: the request it builds, the JSON the daemon answers with, and the mapping back. A test that called
///         the mapping directly would prove the mapping and nothing about the wire it is reached over.
///     </para>
/// </summary>
public sealed class FakeDockerDaemonRouteTests
{
    private const string Image = "busybox@sha256:0000000000000000000000000000000000000000000000000000000000000001";

    [Test]
    public async Task ProbeAsync_ReadsTheDaemonIdentityOffTheVersionAndInfoRoutes()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync(new FakeDockerOptions
        {
            DaemonId = "fakedaemon:identity",
            ServerVersion = "29.4.1",
            ApiVersion = "1.51",
            MinimumApiVersion = "1.24",
            Rootless = true,
            SupportsSeccomp = true
        });

        var identity = await box.Runtime.ProbeAsync();

        AssertEx.Equal("fakedaemon:identity", identity.DaemonId);
        AssertEx.Equal("29.4.1", identity.ServerVersion);
        AssertEx.Equal("1.51", identity.ApiVersion);
        AssertEx.Equal("1.24", identity.MinimumApiVersion);
        AssertEx.Equal("linux", identity.OperatingSystem);

        // Both are read out of the same comma-separated `name=…` groups docker info prints, so getting the rendering
        // wrong would silently report every daemon as rootful and seccomp-less.
        AssertEx.True(identity.IsRootless, "The daemon reported name=rootless and the client did not see it.");
        AssertEx.True(identity.SupportsSeccomp, "The daemon reported name=seccomp and the client did not see it.");
    }

    [Test]
    public async Task ProbeAsync_AgainstADaemonThatReportsNoInstallationId_FailsClosed()
    {
        // A node that cannot pin which daemon it is talking to must not proceed: the attestation is what makes a
        // substituted daemon visible rather than silently accepted.
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.DaemonId = string.Empty;

        var exception = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => box.Runtime.ProbeAsync());

        AssertEx.Equal(DockerDaemonPreflightStatus.ProbeFailed, exception.Status);
    }

    [Test]
    public async Task ImageExistsAsync_IsTrueForASeededImageAndFalseForAnAbsentOne()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.SeedImage(Image);

        AssertEx.True(await box.Runtime.ImageExistsAsync(Image), "A seeded image did not read back as present.");
        AssertEx.False(await box.Runtime.ImageExistsAsync("absent@sha256:" + new string('0', count: 64)),
            "An absent image read back as present, so the 404 the client turns into `false` never arrived.");
    }

    /// <summary>
    ///     The wire half of <c>RealDaemon_PullImage_ReportsProgressAndMakesTheImagePresent</c>, which stays in the
    ///     real-daemon suite as the sentinel for the daemon rewording its own narration.
    /// </summary>
    [Test]
    public async Task PullImageAsync_FoldsTheDaemonsProgressStreamAndLeavesNoLayerOutstanding()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();

        var recorder = new ProgressRecorder<ContainerPullProgress>();
        await box.Runtime.PullImageAsync(Image, recorder);

        AssertEx.True(await box.Runtime.ImageExistsAsync(Image), "The pull completed but the image is not present.");
        AssertEx.NotEmpty(recorder.Reports, "The pull reported no progress at all.");

        var last = recorder.Reports[^1];
        AssertEx.Equal(Image, last.ImageReference);

        // The count is the assertion that matters, and it is the one the opening `Pulling from …` narration line
        // breaks: folded as a layer it never completes, so every finished pull would report n of n plus one.
        AssertEx.Equal(expected: 2, last.LayerCount, "The two-layer default stream did not fold into two layers.");
        AssertEx.Equal(last.LayerCount, last.CompletedLayers,
            $"The pull returned with layers outstanding ({last.CompletedLayers} of {last.LayerCount}).");
    }

    [Test]
    public async Task PullImageAsync_WhenTheStreamCarriesAnError_FailsRatherThanHalfSucceeding()
    {
        // A failed pull still completes the HTTP call normally. The failure is inside the stream, and a client that
        // only checked the status code would report a half-pulled image as installed.
        await using var box = await FakeDockerRuntimeBox.StartAsync();
        box.State.ScriptPull(Image,
            new FakeDockerPullLine
            {
                Status = "Pulling from library/busybox",
                Id = "sha256:0000000000000000000000000000000000000000000000000000000000000001"
            },
            new FakeDockerPullLine
            {
                Error = "manifest unknown"
            });

        var exception = await AssertEx.ThrowsAsync<DockerRuntimeException>(() => box.Runtime.PullImageAsync(Image, progress: null));

        AssertEx.Contains(exception.Message, "manifest unknown");
        AssertEx.False(await box.Runtime.ImageExistsAsync(Image), "A pull that reported an error still made the image present.");
    }

    [Test]
    public async Task PullImageAsync_SplitsTheDigestPinnedReferenceIntoFromImageAndTag()
    {
        await using var box = await FakeDockerRuntimeBox.StartAsync();

        await box.Runtime.PullImageAsync(Image, progress: null);

        AssertEx.Equal("busybox", box.State.LastQueryValue("/images/create", "fromImage"));
        AssertEx.Equal("sha256:0000000000000000000000000000000000000000000000000000000000000001",
            box.State.LastQueryValue("/images/create", "tag"));
    }

    /// <summary>
    ///     Records every report synchronously. Not <see cref="Progress{T}" />: that posts each callback to the
    ///     thread pool, so a list read straight after the pull could be missing reports the pull already made.
    /// </summary>
    private sealed class ProgressRecorder<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _reports = new();

        public IReadOnlyList<T> Reports => [.. _reports];

        public void Report(T value)
        {
            _reports.Enqueue(value);
        }
    }
}
