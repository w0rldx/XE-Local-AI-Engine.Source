namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="RuntimeAcquisitionStatusRegistry" />: the monotonic sequence stamp, the unconditional write, and the
///     push throttle — which deliberately differs from <c>GgufDownloadCoordinator.SetStatus</c> because this lifecycle
///     has several non-terminal phases, not one. Repeated byte updates inside one (phase, step) are throttled; any
///     phase change, any step change, and every terminal status pushes immediately.
/// </summary>
public sealed class RuntimeAcquisitionStatusRegistryTests
{
    [Test]
    public void Current_BeforeAnyReport_IsIdleAtSequenceZero()
    {
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out _);

        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Idle), registry.Current.Phase);
        AssertEx.Equal(expected: 0L, registry.Current.Sequence);
        AssertEx.Empty(publisher.Pushed);
    }

    [Test]
    public void Report_RepeatedByteUpdatesInOneStep_AreThrottled_ButEveryWriteStillLands()
    {
        // The download loop uses an 81 920-byte buffer, so an unthrottled push fires roughly every 80 KB and floods the
        // socket. The WRITE is unconditional regardless, so the hydrate endpoint keeps serving the freshest bytes.
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out _);

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 100, TotalBytes: 900));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 200, TotalBytes: 900));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 300, TotalBytes: 900));

        // Only the phase transition out of Idle was pushed; the two byte repeats were suppressed.
        AssertEx.Equal(expected: 1, publisher.Pushed.Count);
        AssertEx.Equal(expected: 100L, publisher.Pushed[0].CompletedBytes);
        // ...yet the snapshot is fully current, sequence and all.
        AssertEx.Equal(expected: 300L, registry.Current.CompletedBytes);
        AssertEx.Equal(expected: 3L, registry.Current.Sequence);
    }

    [Test]
    public void Report_ByteUpdateAfterTheThrottleInterval_PushesAgain()
    {
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out var time);

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 100));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 200));
        time.Advance(RuntimeAcquisitionStatusRegistry.ProgressPushInterval + TimeSpan.FromMilliseconds(1));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 300));

        AssertEx.Equal(expected: 2, publisher.Pushed.Count);
        AssertEx.Equal(expected: 300L, publisher.Pushed[^1].CompletedBytes);
    }

    [Test]
    public void Report_PhaseTransitionInsideTheThrottleInterval_PushesImmediately()
    {
        // The GGUF rule (bypass only the first and terminal pushes) would swallow this and leave clients sitting on
        // Downloading until completion — the precise staleness this channel exists to remove.
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out _);

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 100));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 200));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Verifying));

        AssertEx.Equal(expected: 2, publisher.Pushed.Count);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Verifying), publisher.Pushed[^1].Phase);
    }

    [Test]
    public void Report_StepTransitionInsideTheThrottleInterval_PushesImmediately()
    {
        // Windows CUDA moves from the build archive to its cudart companion mid-download; a suppressed step transition
        // would show the byte counter restarting with no explanation.
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out _);

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 100, StepIndex: 1, StepCount: 2));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 200, StepIndex: 1, StepCount: 2));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 10, StepIndex: 2, StepCount: 2));

        AssertEx.Equal(expected: 2, publisher.Pushed.Count);
        AssertEx.Equal(expected: 2, publisher.Pushed[^1].StepIndex);
    }

    [Test]
    public void Report_TerminalStatusInsideTheThrottleInterval_PushesImmediately()
    {
        // A suppressed terminal status is unrecoverable for the client: the banner would run forever.
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out _);

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 100));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 200));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Completed));

        AssertEx.Equal(expected: 2, publisher.Pushed.Count);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Completed), publisher.Pushed[^1].Phase);
    }

    [Test]
    public void Report_RepeatedTerminalStatus_StillPushes()
    {
        // "Terminal" is exempted from the throttle by phase, not by novelty, so a re-reported Failed is never swallowed.
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out _);

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Failed, SanitizedError: "first"));
        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Failed, SanitizedError: "second"));

        AssertEx.Equal(expected: 2, publisher.Pushed.Count);
        AssertEx.Equal("second", publisher.Pushed[^1].SanitizedError);
    }

    [Test]
    public void Report_StampsAStrictlyIncreasingSequence_AcrossThrottledAndPushedWrites()
    {
        // The sequence is the client's only defense against a hydrate response racing a push, so it must advance on
        // every write — including the ones whose push was throttled away.
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out _);

        var sequences = new List<long>();
        for (var completed = 1; completed <= 20; completed++)
        {
            registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: completed));
            sequences.Add(registry.Current.Sequence);
        }

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Completed));
        sequences.Add(registry.Current.Sequence);

        AssertEx.True(sequences.Zip(sequences.Skip(1)).All(pair => pair.Second > pair.First),
            "Every status write must be stamped with a strictly greater sequence.");
        AssertEx.Equal(expected: 21L, sequences[^1]);
        AssertEx.True(publisher.Pushed.Zip(publisher.Pushed.Skip(1)).All(pair => pair.Second.Sequence > pair.First.Sequence),
            "Pushed sequences must be strictly increasing too.");
    }

    [Test]
    public void Report_WhenThePublisherThrows_NeitherThrowsNorLosesTheSnapshot()
    {
        // Report is called from the download byte loop and from the startup path; a push failure must never surface
        // there. The hydrate endpoint remains authoritative.
        var registry = new RuntimeAcquisitionStatusRegistry(new ThrowingPublisher(),
            NullLogger<RuntimeAcquisitionStatusRegistry>.Instance,
            new StubTimeProvider());

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading, CompletedBytes: 42));

        AssertEx.Equal(expected: 42L, registry.Current.CompletedBytes);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Downloading), registry.Current.Phase);
    }

    [Test]
    public void Report_CarriesTheVariantTagAndStepContextThrough()
    {
        var publisher = new RecordingPublisher();
        var registry = Build(publisher, out _);

        registry.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Downloading,
            nameof(GpuVariant.Cuda),
            "b10201",
            CompletedBytes: 5,
            TotalBytes: 10,
            StepIndex: 2,
            StepCount: 2));

        var pushed = publisher.Pushed[0];
        AssertEx.Equal(nameof(GpuVariant.Cuda), pushed.Variant);
        AssertEx.Equal("b10201", pushed.Tag);
        AssertEx.Equal(expected: 10L, pushed.TotalBytes);
        AssertEx.Equal(expected: 2, pushed.StepCount);
    }

    [Test]
    public void ProviderRegistration_ResolvesTheRegistryAndTheBinaryManagerThatConsumesIt()
    {
        // The registry ctor takes an OPTIONAL TimeProvider, so the type-based registration only works because the DI
        // activator falls back to the parameter default. Resolve it — and the binary manager whose factory now passes
        // it — rather than trusting that.
        using var http = new HttpClient();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(http);
        services.AddLlamaServerLocalModelProvider();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IRuntimeAcquisitionStatusRegistry>();
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Idle), registry.Current.Phase);
        // The default publisher is the no-op one, so a provider-only host broadcasts nothing.
        AssertEx.True(provider.GetRequiredService<IRuntimeAcquisitionEventPublisher>() is NullRuntimeAcquisitionEventPublisher);
        AssertEx.NotNull(provider.GetRequiredService<ILlamaCppBinaryManager>());
    }

    private static RuntimeAcquisitionStatusRegistry Build(RecordingPublisher publisher, out StubTimeProvider time)
    {
        time = new StubTimeProvider();
        return new RuntimeAcquisitionStatusRegistry(publisher, NullLogger<RuntimeAcquisitionStatusRegistry>.Instance, time);
    }

    /// <summary>A hand-rolled fake clock — the repo does not reference <c>Microsoft.Extensions.TimeProvider.Testing</c>.</summary>
    private sealed class StubTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan delta)
        {
            _now += delta;
        }
    }

    private sealed class RecordingPublisher : IRuntimeAcquisitionEventPublisher
    {
        private readonly Lock _gate = new();
        private readonly List<RuntimeAcquisitionStatusHubEvent> _pushed = [];

        public IReadOnlyList<RuntimeAcquisitionStatusHubEvent> Pushed
        {
            get
            {
                lock (_gate)
                {
                    return [.. _pushed];
                }
            }
        }

        public Task PublishStatusAsync(RuntimeAcquisitionStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _pushed.Add(statusEvent);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher : IRuntimeAcquisitionEventPublisher
    {
        public Task PublishStatusAsync(RuntimeAcquisitionStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The hub connection is gone.");
        }
    }
}
