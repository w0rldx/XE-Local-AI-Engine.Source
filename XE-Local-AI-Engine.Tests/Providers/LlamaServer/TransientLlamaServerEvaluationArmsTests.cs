namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static XE_Local_AI_Engine.Tests.Providers.LlamaServer.TransientLlamaServerEvaluationHarnessTests;

/// <summary>Control vectors and multi-arm runs on the harness, over the real GGUF reader and a fake process launcher.</summary>
[Category(TestCategories.Unit)]
public sealed class TransientLlamaServerEvaluationArmsTests
{
    private const int EmbeddingSize = 1024;
    private const int BlockCount = 24;

    [Test]
    public async Task AVector_IsAppliedOnTheLaunch_AndItsCheckedFactsReturned()
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, BlockCount);
        var vector = files.Vector("direction.15", EmbeddingSize);
        var (harness, supervisor, launcher) = CreateHarness();
        await using (supervisor)
        {
            TransientLlamaServerControlVector? seenInBody = null;
            var result = await harness.RunAsync(Request(model) with
                {
                    ControlVectorPath = vector,
                    ControlVectorScale = -0.5
                },
                static (_, _) => Task.CompletedTask,
                (session, _) =>
                {
                    seenInBody = session.ControlVector;
                    return Task.FromResult(true);
                }, CancellationToken.None);

            var arguments = launcher.Launches.Single().Arguments.ToArray();
            AssertEx.Equal($"{vector}:-0.5", arguments[Array.IndexOf(arguments, "--control-vector-scaled") + 1]);
            var applied = AssertEx.NotNull(result.ControlVector);
            AssertEx.Equal(expected: 15, applied.Layer);
            AssertEx.Equal(expected: EmbeddingSize, applied.ElementCount);
            AssertEx.Equal(-0.5, applied.Scale);
            var file = await FileSha256.ComputeWithLengthAsync(vector, CancellationToken.None);
            AssertEx.Equal(file.Sha256, applied.Sha256);
            AssertEx.Equal(file.SizeBytes, applied.SizeBytes);
            AssertEx.Equal(applied, seenInBody);
        }
    }

    [Test]
    public async Task WithoutAVector_NoControlVectorArgumentReachesTheLaunch()
    {
        using var files = new ControlVectorFiles();
        var (harness, supervisor, launcher) = CreateHarness();
        await using (supervisor)
        {
            var result = await harness.RunAsync(Request(files.Model(EmbeddingSize, BlockCount)),
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None);

            AssertEx.Null(result.ControlVector);
            AssertEx.False(launcher.Launches.Single().Arguments.Any(static argument => argument.StartsWith("--control-vector", StringComparison.Ordinal)));
        }
    }

    /// <summary>Each case is one llama-server drops without failing the start, so nothing but this check stands between it and an unsteered "steered" arm.</summary>
    [Test]
    [Arguments("wrong-size", "has 512 elements")]
    [Arguments("other-model-size", "has 4096 elements")]
    [Arguments("layer-equals-block-count", "targets layer 24")]
    [Arguments("layer-beyond-model", "targets layer 99")]
    [Arguments("layer-zero", "targets layer 0")]
    [Arguments("garbage", "could not be read")]
    [Arguments("missing", "could not be read")]
    [Arguments("empty-path", "must be absolute")]
    [Arguments("truncated", "truncated")]
    [Arguments("two-tensors", "exactly one")]
    [Arguments("not-f32", "exactly one")]
    [Arguments("two-dimensions", "exactly one")]
    [Arguments("wrong-name", "exactly one")]
    [Arguments("non-zero-offset", "exactly one")]
    [Arguments("custom-alignment", "exactly one")]
    public async Task ABadVector_IsRefusedBeforeAnythingStarts(string kind, string expectedMessage)
    {
        using var files = new ControlVectorFiles();
        var request = Request(files.Model(EmbeddingSize, BlockCount)) with
        {
            ControlVectorPath = files.BadVector(kind, EmbeddingSize)
        };
        var admission = new CountingLoadAdmission();
        var (harness, supervisor, launcher) = CreateHarness(GpuVariant.Cuda, loadAdmission: admission);
        await using (supervisor)
        {
            var single = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(request,
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None));
            var arms = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunArmsAsync([Request(request.ModelFilePath), request],
                static (_, _, _) => Task.FromResult(true), CancellationToken.None));

            AssertEx.Contains(single.Message, expectedMessage, StringComparison.Ordinal);
            AssertEx.Contains(arms.Message, expectedMessage, StringComparison.Ordinal);
            AssertEx.Equal(expected: 0, launcher.LaunchCount, "No process may start, not even for the good arm before the bad one.");
            AssertEx.Equal(expected: 0, admission.Acquisitions);
            await AssertLeaseIsFreeAsync(supervisor);
        }
    }

    [Test]
    [Arguments("/tmp/with:colon/vector.gguf", 1.0, "cannot contain")]
    [Arguments("/tmp/with,comma/vector.gguf", 1.0, "cannot contain")]
    public async Task APathTheArgumentCannotCarry_IsRefused(string path, double scale, string expectedMessage)
    {
        using var files = new ControlVectorFiles();
        var (harness, supervisor, launcher) = CreateHarness();
        await using (supervisor)
        {
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(Request(files.Model(EmbeddingSize, BlockCount)) with
                {
                    ControlVectorPath = path,
                    ControlVectorScale = scale
                },
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None));

            AssertEx.Contains(exception.Message, expectedMessage, StringComparison.Ordinal);
            AssertEx.Equal(expected: 0, launcher.LaunchCount);
        }
    }

    /// <summary>Zero applies nothing, and a value outside float range makes llama-server's parser reject its whole argument.</summary>
    [Test]
    [Arguments(0.0)]
    [Arguments(-0.0)]
    [Arguments(double.NaN)]
    [Arguments(double.PositiveInfinity)]
    [Arguments(1E+39)]
    [Arguments(-1E+39)]
    [Arguments(1E-50)]
    public async Task ABadScale_IsRefused(double scale)
    {
        using var files = new ControlVectorFiles();
        var (harness, supervisor, launcher) = CreateHarness();
        await using (supervisor)
        {
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(Request(files.Model(EmbeddingSize, BlockCount)) with
                {
                    ControlVectorPath = files.Vector("direction.15", EmbeddingSize),
                    ControlVectorScale = scale
                },
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None));

            AssertEx.Contains(exception.Message, "scale", StringComparison.Ordinal);
            AssertEx.Equal(expected: 0, launcher.LaunchCount);
        }
    }

    /// <summary>The check would read the path against the host's directory, and the server against the binary's.</summary>
    [Test]
    public async Task ARelativeVectorPath_IsRefused()
    {
        using var files = new ControlVectorFiles();
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, files.Vector("direction.15", EmbeddingSize));
        var (harness, supervisor, launcher) = CreateHarness();
        await using (supervisor)
        {
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(Request(files.Model(EmbeddingSize, BlockCount)) with
                {
                    ControlVectorPath = relative
                },
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None));

            AssertEx.Contains(exception.Message, "must be absolute", StringComparison.Ordinal);
            AssertEx.Equal(expected: 0, launcher.LaunchCount);
        }
    }

    /// <summary>b12014 sets up vector tensors only below <c>n_layer()</c>, which excludes MTP blocks, so a higher layer would be dropped with no error.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AnMtpBlock_CannotBeSteered(bool nextnAsUint64)
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, blockCount: 25, nextnPredictLayers: 1, nextnAsUint64);
        var (harness, supervisor, launcher) = CreateHarness();
        await using (supervisor)
        {
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(Request(model) with
                {
                    ControlVectorPath = files.Vector("direction.24", EmbeddingSize)
                },
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None));

            AssertEx.Contains(exception.Message, "layers 1 to 23", StringComparison.Ordinal);
            AssertEx.Equal(expected: 0, launcher.LaunchCount);

            var allowed = await harness.RunAsync(Request(model) with
                {
                    ControlVectorPath = files.Vector("direction.23", EmbeddingSize)
                },
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None);
            AssertEx.Equal(expected: 23, allowed.ControlVector!.Layer);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AVectorChangedDuringTheLoad_IsRefusedBeforeTheBody(bool delete)
    {
        using var files = new ControlVectorFiles();
        var vector = files.Vector("direction.15", EmbeddingSize);
        var pid = 6000;
        var launcher = new FakeProcessLauncher(_ =>
        {
            if (delete)
            {
                File.Delete(vector);
            }
            else
            {
                var bytes = File.ReadAllBytes(vector);
                bytes[^1] ^= 0xFF;
                File.WriteAllBytes(vector, bytes);
            }

            return new FakeProcessHandle(++pid);
        });
        var (harness, supervisor, _) = CreateHarness(launcher: launcher);
        await using (supervisor)
        {
            var bodyCalled = false;
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunArmsAsync([Request(files.Model(EmbeddingSize, BlockCount)) with
                {
                    ControlVectorPath = vector
                }],
                (_, _, _) =>
                {
                    bodyCalled = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None));

            AssertEx.Contains(exception.Message, "control vector changed", StringComparison.Ordinal);
            AssertEx.False(bodyCalled);
            AssertEx.True(launcher.Handles.Single() is { WasTreeKilled: true, WasDisposed: true });
            await AssertLeaseIsFreeAsync(supervisor);
        }
    }

    /// <summary>The next arm would otherwise spawn beside a process that may still hold VRAM.</summary>
    [Test]
    public async Task Arms_WhenAProcessDidNotExit_DoNotStartTheNextArm()
    {
        using var files = new ControlVectorFiles();
        var request = Request(files.Model(EmbeddingSize, BlockCount)) with
        {
            TeardownTimeout = TimeSpan.FromMilliseconds(20)
        };
        var pid = 7000;
        var launcher = new FakeProcessLauncher(_ => new FakeProcessHandle(++pid, exitOnTreeKill: false));
        var (harness, supervisor, _) = CreateHarness(launcher: launcher);
        await using (supervisor)
        {
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunArmsAsync([request, request],
                static (_, _, _) => Task.FromResult(true), CancellationToken.None));

            AssertEx.Contains(exception.Message, "did not exit", StringComparison.Ordinal);
            AssertEx.Contains(exception.Message, "Arm 0", StringComparison.Ordinal);
            AssertEx.Contains(exception.Message, "process 7001", StringComparison.Ordinal);
            AssertEx.Equal(expected: 1, launcher.LaunchCount);
            await AssertLeaseIsFreeAsync(supervisor);
        }
    }

    /// <summary>A last arm has no next arm to protect, so it keeps reporting the evidence as the single-run path does.</summary>
    [Test]
    public async Task Arms_WhenTheLastProcessDidNotExit_ReturnTheEvidence()
    {
        using var files = new ControlVectorFiles();
        var pid = 7100;
        var (harness, supervisor, _) = CreateHarness(launcher: new FakeProcessLauncher(_ => new FakeProcessHandle(++pid, exitOnTreeKill: false)));
        await using (supervisor)
        {
            var results = await harness.RunArmsAsync([Request(files.Model(EmbeddingSize, BlockCount)) with
                {
                    TeardownTimeout = TimeSpan.FromMilliseconds(20)
                }],
                static (_, _, _) => Task.FromResult(true), CancellationToken.None);

            AssertEx.False(results[0].Teardown.ProcessExitObserved);
            AssertEx.True(results[0].Teardown.ExitObservationTimedOut);
        }
    }

    [Test]
    public async Task PinnedModel_SeesASwappedSymlinkTarget()
    {
        using var files = new ControlVectorFiles();
        var first = files.Model(EmbeddingSize, BlockCount);
        var second = files.Model(EmbeddingSize, BlockCount);
        File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(first));
        var link = first + ".link";
        File.CreateSymbolicLink(link, first);
        var pinned = await TransientLlamaServerPinnedModel.CaptureAsync(link, CancellationToken.None);
        AssertEx.True(pinned.IsUnchanged());

        File.Delete(link);
        File.CreateSymbolicLink(link, second);
        AssertEx.False(pinned.IsUnchanged(), "Another target behind the same link is another model.");
    }

    [Test]
    public async Task PinnedModel_SeesAnEditedSymlinkTarget()
    {
        using var files = new ControlVectorFiles();
        var target = files.Model(EmbeddingSize, BlockCount);
        var link = target + ".link";
        File.CreateSymbolicLink(link, target);
        var pinned = await TransientLlamaServerPinnedModel.CaptureAsync(link, CancellationToken.None);

        File.SetLastWriteTimeUtc(target, DateTime.UnixEpoch);

        AssertEx.False(pinned.IsUnchanged(), "An edit behind the link changes the model the link names.");
    }

    [Test]
    public async Task ARuntimeWithoutTheFlag_RefusesAVector()
    {
        using var files = new ControlVectorFiles();
        var manifest = LlamaServerCapabilityManifest.FromSuccessfulProbe(new LlamaBinary
            {
                ServerExecutablePath = "/fake/bin/llama-server",
                Version = "b1",
                Variant = GpuVariant.Cpu,
                IsPinnedFallback = true
            },
            executableLengthBytes: 1,
            DateTimeOffset.UnixEpoch,
            new string('a', 64),
            "version: 1",
            "--alias --host --port");
        var (harness, supervisor, launcher) = CreateHarness(manifest: manifest);
        await using (supervisor)
        {
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(Request(files.Model(EmbeddingSize, BlockCount)) with
                {
                    ControlVectorPath = files.Vector("direction.15", EmbeddingSize)
                },
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None));

            AssertEx.Contains(exception.Message, "does not support control vectors", StringComparison.Ordinal);
            AssertEx.Equal(expected: 0, launcher.LaunchCount);
        }
    }

    [Test]
    public async Task Arms_ShareOneLease_WithATicketPerArm_InOrder()
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, BlockCount);
        var vector = files.Vector("direction.15", EmbeddingSize);
        var random = files.Vector("direction.15", EmbeddingSize, fill: 0.25f);
        var admission = new TrackingGpuModelLoadAdmission();
        var (harness, supervisor, launcher) = CreateHarness(GpuVariant.Cuda, loadAdmission: admission);
        await using (supervisor)
        {
            Task<IAsyncDisposable?>? competing = null;
            var seen = new List<int>();
            var results = await harness.RunArmsAsync(
                [
                    Request(model),
                    Request(model) with
                    {
                        ControlVectorPath = vector
                    },
                    Request(model) with
                    {
                        ControlVectorPath = random
                    }
                ],
                async (arm, _, _) =>
                {
                    seen.Add(arm);
                    AssertEx.Equal(expected: 1, admission.ActiveTickets, "Each arm holds its own load ticket.");
                    competing ??= WrapLeaseAsync(supervisor);
                    await Task.Yield();
                    AssertEx.False(competing.IsCompleted, "Nothing else may take the runtime between or during arms.");
                    return arm;
                },
                CancellationToken.None);

            AssertEx.Equal("0,1,2", string.Join(',', seen));
            AssertEx.Equal("0,1,2", string.Join(',', results.Select(static result => result.Value)));
            AssertEx.Null(results[0].ControlVector);
            AssertEx.NotEqual(results[1].ControlVector!.Sha256, results[2].ControlVector!.Sha256);
            AssertEx.Equal(expected: 3, launcher.LaunchCount);
            AssertEx.True(launcher.Handles.All(static handle => handle is { WasTreeKilled: true, WasDisposed: true }));
            AssertEx.Equal(expected: 0, admission.ActiveTickets);
            await (await competing!.WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new InvalidOperationException("The lease was not released.")).DisposeAsync();
        }
    }

    /// <summary>The launch rewrites the file with its stamp restored, which only a hash could notice: arms must not notice, so they did not hash again.</summary>
    [Test]
    public async Task Arms_HashTheGgufOnce()
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, BlockCount);
        var original = await FileSha256.ComputeWithLengthAsync(model, CancellationToken.None);
        var launcher = RewritingLauncher(model);
        var (harness, supervisor, _) = CreateHarness(launcher: launcher);
        await using (supervisor)
        {
            var results = await harness.RunArmsAsync([Request(model), Request(model), Request(model)],
                static (_, _, _) => Task.FromResult(true), CancellationToken.None);

            AssertEx.Equal(expected: 3, launcher.LaunchCount);
            AssertEx.True(results.All(result => result.Model.ModelSha256 == original.Sha256), "Every arm reports the one hash taken before the first load.");
        }
    }

    /// <summary>The control for <see cref="Arms_HashTheGgufOnce" />, and the guard that the single-run path keeps its second hash.</summary>
    [Test]
    public async Task SingleRun_StillHashesAfterTheLoad()
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, BlockCount);
        var (harness, supervisor, _) = CreateHarness(launcher: RewritingLauncher(model));
        await using (supervisor)
        {
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunAsync(Request(model),
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.FromResult(true), CancellationToken.None));

            AssertEx.Contains(exception.Message, "changed", StringComparison.Ordinal);
        }
    }

    [Test]
    public async Task Arms_HashEachDistinctGguf()
    {
        using var files = new ControlVectorFiles();
        var first = files.Model(EmbeddingSize, BlockCount);
        var second = files.Model(EmbeddingSize, BlockCount + 1);
        var (harness, supervisor, _) = CreateHarness();
        await using (supervisor)
        {
            var results = await harness.RunArmsAsync([Request(first), Request(second)],
                static (_, _, _) => Task.FromResult(true), CancellationToken.None);

            AssertEx.NotEqual(results[0].Model.ModelSha256, results[1].Model.ModelSha256);
        }
    }

    [Test]
    public async Task Arms_WhenTheFileStampMoves_RefuseThatArm()
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, BlockCount);
        var (harness, supervisor, launcher) = CreateHarness();
        await using (supervisor)
        {
            var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunArmsAsync([Request(model), Request(model)],
                (_, _, _) =>
                {
                    File.SetLastWriteTimeUtc(model, DateTime.UnixEpoch);
                    return Task.FromResult(true);
                },
                CancellationToken.None));

            AssertEx.Contains(exception.Message, "changed", StringComparison.Ordinal);
            AssertEx.Equal(expected: 2, launcher.LaunchCount, "The second arm is the one that notices.");
            AssertEx.True(launcher.Handles.All(static handle => handle is { WasTreeKilled: true, WasDisposed: true }));
            await AssertLeaseIsFreeAsync(supervisor);
        }
    }

    [Test]
    public async Task Arms_WhenAMiddleArmFails_StopAndReleaseEverything()
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, BlockCount);
        var admission = new TrackingGpuModelLoadAdmission();
        var (harness, supervisor, launcher) = CreateHarness(GpuVariant.Cuda, loadAdmission: admission);
        await using (supervisor)
        {
            await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.RunArmsAsync([Request(model), Request(model), Request(model)],
                static (arm, _, _) => arm == 1 ? Task.FromException<bool>(new InvalidOperationException("Synthetic arm failure.")) : Task.FromResult(true),
                CancellationToken.None));

            AssertEx.Equal(expected: 2, launcher.LaunchCount, "The arm after the failure must not start.");
            AssertEx.True(launcher.Handles.All(static handle => handle is { WasTreeKilled: true, WasDisposed: true }));
            AssertEx.Equal(expected: 0, admission.ActiveTickets);
            await AssertLeaseIsFreeAsync(supervisor);
        }
    }

    [Test]
    public async Task Arms_WhenCancelledBetweenArms_StopAndReleaseEverything()
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, BlockCount);
        using var cancellation = new CancellationTokenSource();
        var (harness, supervisor, launcher) = CreateHarness();
        await using (supervisor)
        {
            await AssertEx.ThrowsAsync<OperationCanceledException>(() => harness.RunArmsAsync([Request(model), Request(model)],
                async (_, _, _) =>
                {
                    await cancellation.CancelAsync();
                    return true;
                },
                cancellation.Token));

            AssertEx.Equal(expected: 1, launcher.LaunchCount);
            AssertEx.True(launcher.Handles.Single() is { WasTreeKilled: true, WasDisposed: true });
            await AssertLeaseIsFreeAsync(supervisor);
        }
    }

    [Test]
    public async Task Arms_RefuseAnAdapterAndAnEmptyList()
    {
        using var files = new ControlVectorFiles();
        var model = files.Model(EmbeddingSize, BlockCount);
        var (harness, supervisor, _) = CreateHarness();
        await using (supervisor)
        {
            await AssertEx.ThrowsAsync<ArgumentException>(() => harness.RunArmsAsync([Request(model) with
                {
                    AdapterFilePath = model
                }],
                static (_, _, _) => Task.FromResult(true), CancellationToken.None));
            await AssertEx.ThrowsAsync<ArgumentException>(() => harness.RunArmsAsync<bool>([],
                static (_, _, _) => Task.FromResult(true), CancellationToken.None));
        }
    }

    private static FakeProcessLauncher RewritingLauncher(string modelPath)
    {
        var pid = 5000;
        return new FakeProcessLauncher(_ =>
        {
            var stamp = File.GetLastWriteTimeUtc(modelPath);
            var bytes = File.ReadAllBytes(modelPath);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(modelPath, bytes);
            File.SetLastWriteTimeUtc(modelPath, stamp);
            return new FakeProcessHandle(++pid);
        });
    }

    private static async Task<IAsyncDisposable?> WrapLeaseAsync(LlamaServerProcessSupervisor supervisor) =>
        await supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None);

    private static async Task AssertLeaseIsFreeAsync(LlamaServerProcessSupervisor supervisor)
    {
        var lease = await supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        await (lease ?? throw new InvalidOperationException("The runtime-mutation lease was not released.")).DisposeAsync();
    }
}
