namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The operator-eject mark at decision time: a Background cold launch of an operator-ejected key is refused before
///     capacity admission and the launcher, and the mark lasts until a user-waited load of that key publishes.
/// </summary>
/// <remarks>
///     Every mark check runs against a COLD key, so a refusal or an admission is a real launch decision rather than a
///     reuse of a process that happened to survive.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class SupervisorOperatorEjectMarkTests
{
    private const string Model = "model-a";

    [Test]
    [Arguments("no gate")]
    [Arguments("gate wired")]
    [Arguments("admission published")]
    public async Task BackgroundColdLaunch_OfAnEjectedKey_IsRefusedBeforeAdmissionAndLauncher(string configuration)
    {
        var launcher = new FakeProcessLauncher();
        var registry = new ProcessLaunchAdmissionRegistry();
        var hook = configuration == "no gate" ? null : Substitute.For<ILlamaServerPooledLaunchAdmission>();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            launchAdmissions: registry,
            pooledLaunchAdmission: hook);
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, await OperatorEjectAsync(supervisor));
        hook?.ClearReceivedCalls();
        using var published = configuration == "admission published" ? Publish(registry) : null;

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));

        AssertEx.Equal(expected: 1, launcher.LaunchCount, "A refused Background launch must never reach the launcher.");
        AssertEx.Empty(hook?.ReceivedCalls() ?? [], "A refused Background launch must not be booked against capacity.");
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
    }

    [Test]
    public async Task BackgroundRefusals_LogInformationOncePerEject_ThenDebug()
    {
        var logger = new RecordingLogger<LlamaServerProcessSupervisor>();
        await using var supervisor = SupervisorFactory.Create(logger: logger);

        await OperatorEjectAsync(supervisor);
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
        await OperatorEjectAsync(supervisor);
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));

        var levels = logger.Entries.Where(static entry => entry.Message.Contains("the operator ejected it", StringComparison.Ordinal))
                           .Select(static entry => entry.Level);
        AssertEx.Equal("Information,Debug,Information", string.Join(",", levels));
    }

    [Test]
    public async Task InteractivePublish_ClearsTheMark_SoTheNextColdBackgroundLaunchIsAdmitted()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        await OperatorEjectAsync(supervisor);

        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        await supervisor.EvictAsync(Model, ModelRole.Chat, CancellationToken.None);
        await EnsureBackgroundAsync(supervisor);

        AssertEx.Equal(expected: 3, launcher.LaunchCount, "The Background launch after a user-waited publish must launch.");
    }

    /// <summary>
    ///     Asserts only that the Background spawn is not published: the decision-time refusal or the publication veto,
    ///     so a clear moved to admission time turns this red even while the decision check is broken.
    /// </summary>
    [Test]
    public async Task FailedInteractiveLoad_ClearsNothing()
    {
        var probe = new FakeHealthProbe(ready: false);
        await using var supervisor = SupervisorFactory.Create(healthProbe: probe,
            options: new LlamaServerSupervisorOptions
            {
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 3,
                MaxRestartAttempts = 1
            });
        await OperatorEjectAsync(supervisor);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None));
        probe.Ready = true;

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
        AssertEx.Equal(expected: 0, supervisor.CountRunningProcesses());
    }

    [Test]
    public async Task CancelledInteractiveWaiter_WhoseSpawnPublishes_ClearsTheMark()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new PublicationGatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe);
        await OperatorEjectAsync(supervisor);
        using var cancellation = new CancellationTokenSource();
        var interactive = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, cancellation.Token);
        await probe.Entered;

        await cancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => interactive);
        probe.Release();

        // Joins (or reuses) the detached spawn the cancelled caller started, so it returns once that spawn published.
        await EnsureBackgroundAsync(supervisor);
        await supervisor.EvictAsync(Model, ModelRole.Chat, CancellationToken.None);
        await EnsureBackgroundAsync(supervisor);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task TransientInitiator_WithABackgroundJoin_ClearsTheMark()
    {
        var launcher = new FakeProcessLauncher();
        var probe = new PublicationGatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: probe);
        await OperatorEjectAsync(supervisor);
        var transient = supervisor.EnsureRunningAsync(Model, ModelRole.Chat, ModelResidencyIntent.Transient, CancellationToken.None);
        await probe.Entered;
        var background = EnsureBackgroundAsync(supervisor);
        await AssertEx.StaysIncompleteAsync(background, "The Background caller must join the in-flight transient spawn.");

        probe.Release();
        await Task.WhenAll(transient, background);
        await supervisor.EvictAsync(Model, ModelRole.Chat, CancellationToken.None);
        await EnsureBackgroundAsync(supervisor);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task ProfilingRun_OnAnEjectedKey_IsNotVetoed_AndClearsNothing()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await OperatorEjectAsync(supervisor);

        var measured = await supervisor.RunExclusiveProfilingAsync(Model, ModelRole.Chat, ResolvedLaunchArguments.Explore(), false,
            static (_, _) => Task.FromResult(true), CancellationToken.None);

        AssertEx.True(measured);
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task EjectOfANeverLoadedKey_ReturnsNotRunning_AndStillMarks()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        AssertEx.Equal(LlamaServerEjectOutcome.NotRunning, await OperatorEjectAsync(supervisor));

        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
        AssertEx.Equal(expected: 0, launcher.LaunchCount);
    }

    [Test]
    public async Task TimedOutStillBusyEject_LeavesNoMark()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, options: WithDrain(TimeSpan.FromMilliseconds(50)));
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease(Model, ModelRole.Chat).Lease);

        AssertEx.Equal(LlamaServerEjectOutcome.TimedOutStillBusy, await OperatorEjectAsync(supervisor));
        lease.Dispose();
        await supervisor.EvictAsync(Model, ModelRole.Chat, CancellationToken.None);
        await EnsureBackgroundAsync(supervisor);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    [Test]
    public async Task CancelledDrain_LeavesNoMark()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, options: WithDrain(TimeSpan.FromSeconds(30)));
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease(Model, ModelRole.Chat).Lease);
        using var cancellation = new CancellationTokenSource();
        var eject = supervisor.EjectAsync(Model, ModelRole.Chat, force: false, cancellation.Token, operatorRequested: true);

        await cancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => eject);
        lease.Dispose();
        await supervisor.EvictAsync(Model, ModelRole.Chat, CancellationToken.None);
        await EnsureBackgroundAsync(supervisor);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    /// <summary>A failed attempt rolls back only itself, so a concurrent forced eject keeps the mark whichever finishes first.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task OverlappingEjects_AFailedOneNeverClearsTheForcedOnesMark(bool failedEndsFirst)
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher, options: WithDrain(TimeSpan.FromSeconds(1)));
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);
        var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease(Model, ModelRole.Chat).Lease);
        using var cancellation = new CancellationTokenSource();

        if (failedEndsFirst)
        {
            var graceful = supervisor.EjectAsync(Model, ModelRole.Chat, force: false, cancellation.Token, operatorRequested: true);
            var forced = supervisor.EjectAsync(Model, ModelRole.Chat, force: true, CancellationToken.None, operatorRequested: true);
            await cancellation.CancelAsync();
            await AssertEx.ThrowsAsync<OperationCanceledException>(() => graceful);
            AssertEx.Equal(LlamaServerEjectOutcome.ForcedWhileBusy, await forced);
        }
        else
        {
            var forced = supervisor.EjectAsync(Model, ModelRole.Chat, force: true, CancellationToken.None, operatorRequested: true);
            var graceful = supervisor.EjectAsync(Model, ModelRole.Chat, force: false, cancellation.Token, operatorRequested: true);
            AssertEx.Equal(LlamaServerEjectOutcome.ForcedWhileBusy, await forced);
            await cancellation.CancelAsync();
            try
            {
                AssertEx.Equal(LlamaServerEjectOutcome.TimedOutStillBusy, await graceful);
            }
            catch (OperationCanceledException)
            {
                // Cancelled before its own drain window ran out: the other failure outcome.
            }
        }

        lease.Dispose();
        AssertEx.Equal(expected: 0, supervisor.CountRunningProcesses(), "The forced eject must have left the key cold.");
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task SiblingRoleAndSiblingModel_AreUnaffectedByTheMark()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await OperatorEjectAsync(supervisor);

        await supervisor.EnsureRunningAsync(Model, ModelRole.Embedding, ModelResidencyIntent.Background, CancellationToken.None);
        await supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, ModelResidencyIntent.Background, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        await AssertEx.ThrowsAsync<OperatorEjectedException>(() => EnsureBackgroundAsync(supervisor));
    }

    [Test]
    public async Task NonOperatorEject_LeavesNoMark()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(LlamaServerEjectOutcome.Ejected,
            await supervisor.EjectAsync(Model, ModelRole.Chat, force: false, CancellationToken.None, operatorRequested: false));
        await EnsureBackgroundAsync(supervisor);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }

    private static Task<LlamaServerEjectOutcome> OperatorEjectAsync(LlamaServerProcessSupervisor supervisor) =>
        supervisor.EjectAsync(Model, ModelRole.Chat, force: false, CancellationToken.None, operatorRequested: true);

    private static Task<LlamaServerEndpoint> EnsureBackgroundAsync(LlamaServerProcessSupervisor supervisor) =>
        supervisor.EnsureRunningAsync(Model, ModelRole.Chat, ModelResidencyIntent.Background, CancellationToken.None);

    private static LlamaServerSupervisorOptions WithDrain(TimeSpan drain) =>
        new()
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 3,
            MaxRestartAttempts = 3,
            EjectDrainTimeout = drain
        };

    /// <summary>Publishes the admission a caller that ran its own capacity decision would leave for the launch to consume.</summary>
    private static IDisposable Publish(ProcessLaunchAdmissionRegistry registry)
    {
        AssertEx.True(registry.TryAcquire(new ProcessLaunchAdmission
        {
            ModelName = Model,
            Role = ModelRole.Chat,
            Variant = GpuVariant.Cpu,
            ResolvedArguments = ResolvedLaunchArguments.Explore(),
            Allocation = new ProcessContextAllocation
            {
                ProcessContextTokens = 8192,
                ModelTrainContextTokens = 131072,
                Source = ProcessContextAllocationSource.HardwareTier,
                Placement = ProcessPlacementMode.Cpu,
                Footprint = ResourceFootprint.Zero,
                ContentIdentity = $"{Model}:0",
                CacheKey = $"cache:{Model}"
            }
        }, out var consumer));
        return consumer!;
    }
}
