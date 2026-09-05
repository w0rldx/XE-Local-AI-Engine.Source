namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class TransientLlamaServerEvaluationHarnessTests
{
    [Test]
    public async Task Evaluation_HoldsRuntimeMutationGateThroughBodyAndTeardown()
    {
        var modelPath = await CreateModelFileAsync("base-model");
        try
        {
            var bodyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (harness, supervisor, _) = CreateHarness();
            await using (supervisor)
            {
                var evaluation = harness.RunAsync(Request(modelPath),
                    static (_, _) => Task.CompletedTask,
                    async (_, ct) =>
                    {
                        bodyEntered.SetResult();
                        await releaseBody.Task.WaitAsync(ct);
                        return true;
                    }, CancellationToken.None);
                await bodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

                var competingMutation = supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
                await Task.Yield();
                AssertEx.False(competingMutation.IsCompleted,
                    "A runtime mutation must wait until the evaluation process and its teardown have completed.");

                releaseBody.SetResult();
                var result = await evaluation.WaitAsync(TimeSpan.FromSeconds(3));
                AssertEx.True(result.Value);
                var lease = await competingMutation.WaitAsync(TimeSpan.FromSeconds(3));
                AssertEx.NotNull(lease);
                await (lease ?? throw new InvalidOperationException("The competing mutation lease was not granted.")).DisposeAsync();
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Test]
    public async Task Evaluation_WhenSupervisedModelIsWarm_FailsClosedWithoutTransientSpawn()
    {
        var modelPath = await CreateModelFileAsync("candidate");
        try
        {
            var (harness, supervisor, launcher) = CreateHarness();
            await using (supervisor)
            {
                await supervisor.EnsureRunningAsync("warm-model", ModelRole.Chat, CancellationToken.None);

                var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(Request(modelPath),
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.FromResult(true), CancellationToken.None));

                AssertEx.Contains(exception.Message, "loaded", StringComparison.OrdinalIgnoreCase);
                AssertEx.Equal(expected: 1, launcher.LaunchCount);
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Test]
    public async Task Evaluation_ReturnsTeardownEvidenceAfterTreeKillAndDispose()
    {
        var modelPath = await CreateModelFileAsync("candidate");
        try
        {
            var (harness, supervisor, launcher) = CreateHarness();
            await using (supervisor)
            {
                var result = await harness.RunAsync(Request(modelPath),
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.FromResult(42), CancellationToken.None);

                AssertEx.Equal(expected: 42, result.Value);
                AssertEx.True(result.Teardown.TreeKillRequested);
                AssertEx.True(result.Teardown.ProcessExitObserved);
                AssertEx.True(result.Teardown.HandleDisposed);
                var handle = launcher.Handles.Single();
                AssertEx.True(handle.WasTreeKilled);
                AssertEx.True(handle.WasDisposed);
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Test]
    public async Task Evaluation_WhenBodyThrows_StillTreeKillsAndDisposes()
    {
        var modelPath = await CreateModelFileAsync("candidate");
        try
        {
            var (harness, supervisor, launcher) = CreateHarness();
            await using (supervisor)
            {
                await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync(Request(modelPath),
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.FromException<bool>(new InvalidOperationException("Synthetic scoring failure.")),
                    CancellationToken.None));

                var handle = launcher.Handles.Single();
                AssertEx.True(handle.WasTreeKilled);
                AssertEx.True(handle.WasDisposed);
                var mutationLease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
                AssertEx.NotNull(mutationLease);
                await (mutationLease ?? throw new InvalidOperationException("The evaluation mutation lease was not released.")).DisposeAsync();
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Test]
    public async Task Evaluation_BaseAndTunedExposeIdenticalFrozenLaunchPolicyAndDistinctModelProvenance()
    {
        var basePath = await CreateModelFileAsync("base-model");
        var tunedPath = await CreateModelFileAsync("tuned-model");
        try
        {
            var (harness, supervisor, _) = CreateHarness(GpuVariant.Cuda);
            await using (supervisor)
            {
                var baseline = await harness.RunAsync(Request(basePath),
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.FromResult(true),
                    CancellationToken.None);
                var tuned = await harness.RunAsync(Request(tunedPath),
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.FromResult(true),
                    CancellationToken.None);

                AssertEx.Equal(baseline.Launch.Variant, tuned.Launch.Variant);
                AssertEx.True(string.Equals(baseline.Launch.ExecutableVersion, tuned.Launch.ExecutableVersion, StringComparison.Ordinal));
                AssertEx.True(string.Equals(baseline.Launch.ManifestSha256, tuned.Launch.ManifestSha256, StringComparison.Ordinal));
                AssertEx.Equal(baseline.Launch.LaunchProjection, tuned.Launch.LaunchProjection);
                AssertEx.Equal(baseline.Launch.BenchmarkLaunchPolicy, tuned.Launch.BenchmarkLaunchPolicy);
                AssertEx.Equal(LlamaServerBenchmarkLaunchPolicy.DeterministicV1, tuned.Launch.BenchmarkLaunchPolicy);
                AssertEx.NotEqual(baseline.Model.ModelSha256, tuned.Model.ModelSha256);
                AssertEx.Null(baseline.Model.AdapterSha256);
                AssertEx.Null(tuned.Model.AdapterSha256);
            }
        }
        finally
        {
            File.Delete(basePath);
            File.Delete(tunedPath);
        }
    }

    [Test]
    public async Task Evaluation_DelayedExit_HoldsMutationAndGpuAdmissionUntilExitIsObserved()
    {
        var modelPath = await CreateModelFileAsync("candidate");
        try
        {
#pragma warning disable CA2000 // Ownership transfers to the fake launcher, then to the evaluation harness under test.
            var handle = new FakeProcessHandle(pid: 4242, exitOnTreeKill: false);
#pragma warning restore CA2000
            var launcher = new FakeProcessLauncher(_ => handle);
            var admission = new TrackingGpuModelLoadAdmission();
            var (harness, supervisor, _) = CreateHarness(GpuVariant.Cuda, launcher, loadAdmission: admission);
            await using (supervisor)
            {
                var evaluation = harness.RunAsync(Request(modelPath),
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.FromResult(true),
                    CancellationToken.None);
                await WaitUntilAsync(() => handle.WasTreeKilled);

                var competingMutation = supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
                await Task.Yield();
                AssertEx.False(evaluation.IsCompleted);
                AssertEx.False(competingMutation.IsCompleted);
                AssertEx.Equal(expected: 1, admission.ActiveTickets);

                handle.SimulateExit();
                var result = await evaluation.WaitAsync(TimeSpan.FromSeconds(3));
                AssertEx.True(result.Teardown.ProcessExitObserved);
                AssertEx.False(result.Teardown.ExitObservationTimedOut);
                AssertEx.Equal(expected: 0, admission.ActiveTickets);
                var lease = await competingMutation.WaitAsync(TimeSpan.FromSeconds(3));
                await (lease ?? throw new InvalidOperationException("The mutation gate was not released after observed exit.")).DisposeAsync();
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Test]
    public async Task Evaluation_ExitObservationTimeout_ReturnsExplicitEvidenceBeforeReleasingLeases()
    {
        var modelPath = await CreateModelFileAsync("candidate");
        try
        {
#pragma warning disable CA2000 // Ownership transfers to the fake launcher, then to the evaluation harness under test.
            var handle = new FakeProcessHandle(pid: 4242, exitOnTreeKill: false);
#pragma warning restore CA2000
            var launcher = new FakeProcessLauncher(_ => handle);
            var admission = new TrackingGpuModelLoadAdmission();
            var request = Request(modelPath) with
            {
                TeardownTimeout = TimeSpan.FromMilliseconds(20)
            };
            var (harness, supervisor, _) = CreateHarness(GpuVariant.Cuda, launcher, loadAdmission: admission);
            await using (supervisor)
            {
                var result = await harness.RunAsync(request,
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.FromResult(true),
                    CancellationToken.None);

                AssertEx.True(result.Teardown.TreeKillRequested);
                AssertEx.False(result.Teardown.ProcessExitObserved);
                AssertEx.True(result.Teardown.ExitObservationTimedOut);
                AssertEx.True(result.Teardown.HandleDisposed);
                AssertEx.Equal(expected: 0, admission.ActiveTickets);
                var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
                await (lease ?? throw new InvalidOperationException("The mutation gate was not released after bounded exit timeout.")).DisposeAsync();
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Test]
    public async Task Evaluation_BindsValidatedProvenanceAndEndpointAliasBeforeScoringBody()
    {
        var modelPath = await CreateModelFileAsync("candidate");
        try
        {
            var healthProbe = new FakeHealthProbe();
            var (harness, supervisor, launcher) = CreateHarness(healthProbe: healthProbe);
            await using (supervisor)
            {
                TransientLlamaServerEvaluationProvenance? bound = null;
                var result = await harness.RunAsync(Request(modelPath),
                    (provenance, _) =>
                    {
                        bound = provenance;
                        return Task.CompletedTask;
                    },
                    (session, _) =>
                    {
                        AssertEx.NotNull(bound);
                        AssertEx.Equal(bound, session.Provenance);
                        AssertEx.True(string.Equals(healthProbe.ExpectedModelAlias, session.ModelId, StringComparison.Ordinal));
                        return Task.FromResult(true);
                    }, CancellationToken.None);

                AssertEx.NotNull(bound);
                AssertEx.Equal(bound, result.Provenance);
                var arguments = launcher.Launches.Single().Arguments.ToArray();
                var aliasIndex = Array.IndexOf(arguments, "--alias");
                AssertEx.True(aliasIndex >= 0 && aliasIndex + 1 < arguments.Length);
                AssertEx.True(string.Equals(healthProbe.ExpectedModelAlias, arguments[aliasIndex + 1], StringComparison.Ordinal));
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Test]
    public async Task Evaluation_WhenEndpointAliasDoesNotMatch_FailsBeforeBindingOrScoring()
    {
        var modelPath = await CreateModelFileAsync("candidate");
        try
        {
            var healthProbe = new FakeHealthProbe
            {
                EndpointIdentityMatches = false
            };
            var (harness, supervisor, launcher) = CreateHarness(healthProbe: healthProbe);
            await using (supervisor)
            {
                var binderCalled = false;
                var bodyCalled = false;
                await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(Request(modelPath),
                    (_, _) =>
                    {
                        binderCalled = true;
                        return Task.CompletedTask;
                    },
                    (_, _) =>
                    {
                        bodyCalled = true;
                        return Task.FromResult(true);
                    }, CancellationToken.None));

                AssertEx.False(binderCalled);
                AssertEx.False(bodyCalled);
                var handle = launcher.Handles.Single();
                AssertEx.True(handle.WasTreeKilled);
                AssertEx.True(handle.WasDisposed);
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    /// <summary>
    ///     A recorded source build outranks the requested variant, so the serve can hand back a GPU build under a Cpu
    ///     selection (the selector reads Cpu whenever the GPU probe times out or finds no vendor). The evaluation must
    ///     then take the VRAM admission ticket and spawn as the GPU build it actually is — keying either off the
    ///     selector loads a CUDA build onto the device without ever entering the gate.
    /// </summary>
    [Test]
    public async Task Evaluation_WhenServedBuildIsGpu_TakesLoadTicketAndSpawnsAsGpu()
    {
        var modelPath = await CreateModelFileAsync("candidate");
        try
        {
            var admission = new CountingLoadAdmission();
            var (harness, supervisor, launcher) = CreateHarness(GpuVariant.Cpu,
                loadAdmission: admission,
                servedVariant: GpuVariant.Cuda);
            await using (supervisor)
            {
                await harness.RunAsync(Request(modelPath),
                    static (_, _) => Task.CompletedTask,
                    static (_, _) => Task.FromResult(true), CancellationToken.None);

                AssertEx.Equal(expected: 1, admission.Acquisitions);
                AssertEx.True(launcher.Launches.TryDequeue(out var spec));
                AssertEx.True(spec!.Arguments.Contains("--metrics"), "A served GPU build must spawn in GPU mode.");
                AssertEx.False(spec.Arguments.Contains("-t"), "A served GPU build must not take the CPU thread policy.");
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    /// <summary>The same rule on the smoke-load path, which composes its own spec off the variant.</summary>
    [Test]
    public async Task TransientRun_WhenServedBuildIsGpu_SpawnsAsGpu()
    {
        var modelPath = await CreateModelFileAsync("smoke");
        try
        {
            var launcher = new FakeProcessLauncher();
            var transient = new TransientLlamaServerLauncher(new FakeBinaryManager(GpuVariant.Cuda),
                new FakeVariantSelector(GpuVariant.Cpu),
                launcher,
                new FakeHealthProbe(),
                NullLogger<TransientLlamaServerLauncher>.Instance);

            await transient.RunAsync(new TransientLlamaServerRequest(modelPath, AdapterFilePath: null, ContextTokens: 2048, TimeSpan.FromSeconds(5)),
                static (_, _) => Task.FromResult(true), CancellationToken.None);

            AssertEx.True(launcher.Launches.TryDequeue(out var spec));
            AssertEx.True(spec!.Arguments.Contains("--metrics"), "A served GPU build must spawn in GPU mode.");
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    private sealed class CountingLoadAdmission : IGpuModelLoadAdmission
    {
        public int Acquisitions { get; private set; }

        public Task<IDisposable> AcquireAsync(CancellationToken ct)
        {
            Acquisitions++;
            return Task.FromResult<IDisposable>(new Ticket());
        }

        private sealed class Ticket : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static (ITransientLlamaServerEvaluationHarness Harness, LlamaServerProcessSupervisor Supervisor, FakeProcessLauncher Launcher) CreateHarness(GpuVariant variant = GpuVariant.Cpu,
        FakeProcessLauncher? launcher = null,
        FakeHealthProbe? healthProbe = null,
        IGpuModelLoadAdmission? loadAdmission = null,
        GpuVariant? servedVariant = null)
    {
        launcher ??= new FakeProcessLauncher();
        var variantSelector = new FakeVariantSelector(variant);
        var binaryManager = new FakeBinaryManager(servedVariant);
        healthProbe ??= new FakeHealthProbe();
        var manifestProbe = new FakeLlamaServerCapabilityManifestProbe();
        var launchPolicy = new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions(), new FakeLaunchFallbackStore());
        var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: variantSelector,
            launchPolicy: launchPolicy,
            capabilityManifestProbe: manifestProbe);
        var transient = new TransientLlamaServerLauncher(binaryManager,
            variantSelector,
            launcher,
            healthProbe,
            NullLogger<TransientLlamaServerLauncher>.Instance);
        var harness = new TransientLlamaServerEvaluationHarness(supervisor,
            binaryManager,
            variantSelector,
            manifestProbe,
            launchPolicy,
            transient,
            loadAdmission ?? new NoOpGpuModelLoadAdmission());
        return (harness, supervisor, launcher);
    }

    private static TransientLlamaServerEvaluationRequest Request(string modelPath) =>
        new(modelPath,
            AdapterFilePath: null,
            ContextTokens: 4096,
            TimeSpan.FromMinutes(1),
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);

    private static Task WaitUntilAsync(Func<bool> predicate) =>
        AssertEx.EventuallyAsync(predicate, TestBudgets.Contended, "The awaited harness state never arrived.");

    private static async Task<string> CreateModelFileAsync(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"xe-evaluation-{Guid.NewGuid():N}.gguf");
        await File.WriteAllTextAsync(path, contents);
        return path;
    }

    private sealed class TrackingGpuModelLoadAdmission : IGpuModelLoadAdmission
    {
        private int _activeTickets;

        public int ActiveTickets => Volatile.Read(ref _activeTickets);

        public Task<IDisposable> AcquireAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _activeTickets);
            return Task.FromResult<IDisposable>(new Ticket(this));
        }

        private sealed class Ticket(TrackingGpuModelLoadAdmission owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
                {
                    Interlocked.Decrement(ref owner._activeTickets);
                }
            }
        }
    }
}
