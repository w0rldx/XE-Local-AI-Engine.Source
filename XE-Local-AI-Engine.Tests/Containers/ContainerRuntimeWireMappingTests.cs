namespace XE_Local_AI_Engine.Tests.Containers;

using System.Buffers.Binary;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two pieces of the production client that read meaning out of daemon text, tested without a daemon.
///     <para>
///         Both exist because 4.3.3's models do not carry the value the engine needs in a typed field: the list
///         response has no exit code and the health state is a free string. They are the places a Docker release could
///         change wording underneath us, which is why the real-daemon suite asserts the prose and these assert the
///         readers.
///     </para>
///     <para>
///         The production client's argument guards are NOT asserted here. Hand-copying them beside the fake's copies
///         is what let two drifts through; they are asserted once, against both implementations, in
///         <see cref="ContainerRuntimeContractTests" />.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerRuntimeWireMappingTests
{
    /// <summary>The first header byte of a standard-output chunk in Docker's exec stream framing.</summary>
    private const byte StandardOutputFrame = 1;

    /// <summary>The same for standard error.</summary>
    private const byte StandardErrorFrame = 2;

    [Test]
    [Arguments("Exited (137) 3 minutes ago", 137)]
    [Arguments("Exited (0) About an hour ago", 0)]
    [Arguments("exited (7) 2 seconds ago", 7)]
    public void ParseExitCode_ReadsTheCodeOutOfTheDaemonsStatusProse(string status, int expected)
    {
        AssertEx.Equal(expected, DockerDotNetRuntimeClient.ParseExitCode(status));
    }

    [Test]
    [Arguments("Up 4 minutes")]
    [Arguments("Up 2 hours (healthy)")]
    [Arguments("Created")]
    [Arguments("Exited 3 minutes ago")]
    [Arguments("Exited (abc) 3 minutes ago")]
    [Arguments("Exited (137 3 minutes ago")]
    [Arguments("")]
    [Arguments(null)]
    public void ParseExitCode_OnAnyOtherShape_IsNullAndNeverZero(string? status)
    {
        // Null means "the daemon did not say". Reading an unparseable status as 0 would report a crashed application
        // as a clean exit, which is the one wrong answer this parser must never give.
        AssertEx.Null(DockerDotNetRuntimeClient.ParseExitCode(status));
    }

    [Test]
    [Arguments("starting", ContainerHealthState.Starting)]
    [Arguments("STARTING", ContainerHealthState.Starting)]
    [Arguments("healthy", ContainerHealthState.Healthy)]
    [Arguments("Healthy", ContainerHealthState.Healthy)]
    [Arguments("unhealthy", ContainerHealthState.Unhealthy)]
    public async Task ToHealthState_MapsTheDaemonsWordsOrdinalIgnoreCase(string status, ContainerHealthState expected)
    {
        var recorder = new RecordingLogger();
        await using var client = ClientWith(recorder);

        AssertEx.Equal(expected, client.ToHealthState(status));
        AssertEx.Empty(recorder.Warnings);
    }

    [Test]
    public async Task ToHealthState_OnNoHealthcheck_IsNoneAndSilent()
    {
        var recorder = new RecordingLogger();
        await using var client = ClientWith(recorder);

        AssertEx.Equal(ContainerHealthState.None, client.ToHealthState(status: null));
        AssertEx.Empty(recorder.Warnings);
    }

    [Test]
    public async Task ToHealthState_OnAnUnrecognisedString_IsNoneAndWarnsOncePerClient()
    {
        var recorder = new RecordingLogger();
        await using var client = ClientWith(recorder);

        AssertEx.Equal(ContainerHealthState.None, client.ToHealthState("degraded"));
        AssertEx.Equal(ContainerHealthState.None, client.ToHealthState("degraded"));
        AssertEx.Equal(ContainerHealthState.None, client.ToHealthState("quiescing"));

        // Once, not never and not per inspect: a renamed daemon state must be visible, and a poll loop asking every
        // second would otherwise fill the node log with the same line. The caller reads None from a service that
        // declared a healthcheck as "not yet healthy", so an unrecognised word delays to the deadline rather than
        // passing as healthy.
        AssertEx.Equal(expected: 1, recorder.Warnings.Count);
        AssertEx.Contains(recorder.Warnings[0], "degraded");
    }

    /// <summary>
    ///     The aggregator, which is the only place the daemon's per-layer chatter becomes a number a user sees.
    /// </summary>
    [Test]
    public void PullProgress_SumsLayersAndCountsTheCompleteOnes()
    {
        var time = new AdvanceableTimeProvider();
        var aggregator = new PullProgressAggregator("image@sha256:abc", time);

        aggregator.Report(Layer("a", "Downloading", current: 50, total: 100));
        aggregator.Report(Layer("b", "Downloading", current: 20, total: 200));
        aggregator.Report(Layer("a", "Download complete", current: null, total: null));

        var snapshot = aggregator.Snapshot();

        AssertEx.Equal(expected: 2, snapshot.LayerCount);
        AssertEx.Equal(expected: 1, snapshot.CompletedLayers);
        // A completed layer counts its whole total, so the figure does not go backwards when the daemon stops
        // reporting bytes for it.
        AssertEx.Equal(expected: 120L, snapshot.CurrentBytes);
        AssertEx.Equal(expected: 300L, snapshot.TotalBytes);
    }

    [Test]
    public void PullProgress_TheOpeningNarrationLine_IsNotCountedAsALayer()
    {
        // The daemon opens every pull with "Pulling from library/<name>", and that line carries an id — the tag or
        // digest that was asked for — so an aggregator that treats every id-carrying message as a layer gains one
        // that never completes. A finished pull would then report n of n+1 layers forever. Found against a real
        // daemon by ContainerRuntimeRealDaemonTests; pinned here so it stays fixed without one.
        var time = new AdvanceableTimeProvider();
        var aggregator = new PullProgressAggregator("image@sha256:abc", time);

        aggregator.Report(Layer("1.37", "Pulling from library/busybox", current: null, total: null));
        aggregator.Report(Layer("a", "Already exists", current: null, total: null));

        var snapshot = aggregator.Snapshot();

        AssertEx.Equal(expected: 1, snapshot.LayerCount);
        AssertEx.Equal(snapshot.LayerCount, snapshot.CompletedLayers);
    }

    [Test]
    public void PullProgress_ALayerWithNoReportedTotal_ContributesZeroRatherThanBeingDropped()
    {
        var time = new AdvanceableTimeProvider();
        var aggregator = new PullProgressAggregator("image@sha256:abc", time);

        aggregator.Report(Layer("a", "Downloading", current: null, total: null));

        // Dropping it would shrink the denominator and make the pull look further along than it is.
        AssertEx.Equal(expected: 1, aggregator.Snapshot().LayerCount);
        AssertEx.Equal(expected: 0L, aggregator.Snapshot().TotalBytes);
    }

    [Test]
    public void PullProgress_EmitsAtMostOneReportPerInterval()
    {
        var time = new AdvanceableTimeProvider();
        var aggregator = new PullProgressAggregator("image@sha256:abc", time);

        var first = aggregator.Report(Layer("a", "Downloading", current: 1, total: 100));
        var throttled = aggregator.Report(Layer("a", "Downloading", current: 2, total: 100));
        time.Advance(PullProgressAggregator.EmissionInterval);
        var afterInterval = aggregator.Report(Layer("a", "Downloading", current: 3, total: 100));

        // The daemon emits hundreds of messages a second and the caller relays each report over a hub, so the
        // throttle is what stops a pull spending itself on serialising status text.
        AssertEx.NotNull(first);
        AssertEx.Null(throttled);
        AssertEx.NotNull(afterInterval);
    }

    [Test]
    public void PullProgress_CapturesTheStreamsErrorObjectsMessage()
    {
        var time = new AdvanceableTimeProvider();
        var aggregator = new PullProgressAggregator("image@sha256:abc", time);

        aggregator.Report(new JSONMessage
        {
            Error = new JSONError
            {
                Message = "manifest unknown"
            }
        });

        // Error is a JSONError object rather than a string, so reading the object itself would render a type name
        // into an operator-facing failure. A pull that fails part-way still completes the HTTP call normally, which
        // is why this is captured at all rather than surfacing as an exception.
        AssertEx.Equal("manifest unknown", aggregator.Error);
    }

    /// <summary>
    ///     A clock the test moves, so the throttle is asserted by advancing time rather than by waiting for it: a
    ///     sleeping test would be slow when it passed and flaky when it did not.
    /// </summary>
    private sealed class AdvanceableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan amount)
        {
            _now += amount;
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ReadBounded_OverAMultiMegabyteStream_KeepsTheCeilingAndReportsTruncation(bool onStandardError)
    {
        // The process on the other end of an exec lives in an image the engine did not build, and it decides how much
        // it writes. Truncating after a read-to-end would let it decide how much of this node's memory is used and
        // then throw most of it away; the ceiling has to hold DURING the read. Four megabytes against a 4 KiB
        // ceiling is a thousandfold, which is the ratio that makes the difference observable at all.
        const int CeilingBytes = 4 * 1024;
        const int PayloadBytes = 4 * 1024 * 1024;

        using var framed = new MemoryStream(Framed(onStandardError ? StandardErrorFrame : StandardOutputFrame, PayloadBytes));
        using var stream = new MultiplexedStream(framed, multiplexed: true);

        var captured = await DockerDotNetRuntimeClient.ReadBoundedAsync(stream, CeilingBytes, CancellationToken.None);

        var (text, truncated) = onStandardError
            ? (captured.StandardError, captured.StandardErrorTruncated)
            : (captured.StandardOutput, captured.StandardOutputTruncated);

        AssertEx.Equal(CeilingBytes, text.Length);
        AssertEx.True(truncated, "Four megabytes were read against a 4 KiB ceiling and nothing was reported as dropped.");

        // The other stream said nothing, and must not have inherited this one's bytes.
        var otherText = onStandardError ? captured.StandardOutput : captured.StandardError;
        AssertEx.Equal(string.Empty, otherText);
    }

    /// <summary>
    ///     A Docker exec stream as the daemon frames one: an eight-byte header per chunk whose first byte names the
    ///     target stream and whose last four are the chunk length, big-endian.
    /// </summary>
    private static byte[] Framed(byte targetStream, int payloadBytes)
    {
        const int ChunkBytes = 64 * 1024;
        using var buffer = new MemoryStream();

        for (var written = 0; written < payloadBytes; written += ChunkBytes)
        {
            var size = Math.Min(ChunkBytes, payloadBytes - written);
            var header = new byte[8];
            header[0] = targetStream;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(start: 4), size);
            buffer.Write(header);
            buffer.Write(new byte[size]);
        }

        return buffer.ToArray();
    }


    private static JSONMessage Layer(string id, string status, long? current, long? total)
    {
        return new JSONMessage
        {
            ID = id,
            Status = status,
            Progress = new JSONProgress
            {
                Current = current,
                Total = total
            }
        };
    }

    [Test]
    public async Task ToInspection_ReadsTheImageReferenceTheContainerWasCreatedWith_NotTheResolvedImageId()
    {
        // The daemon reports both, and they answer different questions. Config.Image is the reference the container
        // was created with, which is what a verifier holds; the top-level Image is the resolved image id, which
        // matches nothing an application manifest names. The fake seam answers with the requested reference, so
        // reading the id here would make the two implementations disagree about what the field means — and no
        // fake-based test could catch it.
        const string Reference = "busybox@sha256:9db7b59979c38555a39def84a31fb98b5296952f9e3afd4f6f11f05b07adfab0";

        await using var client = ClientWith(NullLogger.Instance);

        var inspection = client.ToInspection("container-a",
            new ContainerInspectResponse
            {
                ID = "container-a",
                Name = "/xe-app",
                Image = "sha256:0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0",
                Config = new ContainerConfig
                {
                    Image = Reference
                },
                HostConfig = new HostConfig()
            });

        AssertEx.Equal(Reference, inspection.Image);
        AssertEx.Equal("xe-app", inspection.Name);
    }

    [Test]
    public async Task ToInspection_WithNoHostConfig_IsARefusalNamingTheContainer()
    {
        // Without the host configuration there is nothing to verify the isolation settings against, and a partially
        // populated inspection would be read as evidence that they were applied.
        await using var client = ClientWith(NullLogger.Instance);

        var failure = AssertEx.Throws<DockerRuntimeException>(() => client.ToInspection("container-b",
            new ContainerInspectResponse
            {
                ID = "container-b"
            }));

        AssertEx.Equal(DockerDaemonPreflightStatus.ProbeFailed, failure.Status);
        AssertEx.Contains(failure.Message, "container-b");
    }

    private static DockerDotNetRuntimeClient ClientWith(ILogger logger)
    {
        // Never connected: DockerClientBuilder opens no socket at construction, and no member that touches the wire
        // is called here.
        return new DockerDotNetRuntimeClient(new DockerDaemonEndpoint(new Uri("unix:///xe-health-mapping-tests.sock"), DockerDaemonEndpointSource.Configuration),
            TimeSpan.FromSeconds(1),
            TimeProvider.System,
            requestTimeout: null,
            pullTimeout: null,
            logger);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
