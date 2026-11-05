namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The harness on a real <c>llama-server</c> and GPU: vector arms under one lease, refusals, a failed arm and cancellation. Opt-in: set
///     <c>XE_MODEL_LAB_LIVE_SERVER</c>, <c>_GGUF</c>, <c>_OTHER_GGUF</c>, <c>_VECTOR</c>, <c>_RANDOM_VECTOR</c> and <c>_OUT</c> (a JSON evidence file).
/// </summary>
[Category(TestCategories.ExternalInfra)]
public sealed class ModelLabHarnessLiveTests
{
    private const string Prompt = "Write one sentence about the weather today.";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true
    };

    [Test]
    public async Task VectorArms_OnTheRealRuntime()
    {
        var server = Require("SERVER");
        var gguf = Require("GGUF");
        var otherGguf = Require("OTHER_GGUF");
        var vector = Require("VECTOR");
        var randomVector = Require("RANDOM_VECTOR");
        var outPath = Require("OUT");
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var files = new ControlVectorFiles();
        using var http = new HttpClient();
        var launches = new RecordingLauncher(new LlamaServerProcessLauncher(NullLogger<LlamaServerProcessLauncher>.Instance, new ChildProcessOutputTailRegistry(TimeProvider.System)));
        var variantSelector = new FakeVariantSelector(GpuVariant.Cuda);
        var binaryManager = new FakeBinaryManager(GpuVariant.Cuda, server);
        var healthProbe = new LlamaServerHealthProbe(http);
        var manifestProbe = new LlamaServerCapabilityManifestProbe(NullLogger<LlamaServerCapabilityManifestProbe>.Instance);
        var launchPolicy = new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions(), new FakeLaunchFallbackStore());
        var supervisor = SupervisorFactory.Create(launches,
            healthProbe,
            new FakeModelStore(gguf),
            variantSelector: variantSelector,
            launchPolicy: launchPolicy,
            capabilityManifestProbe: manifestProbe,
            binaryManager: binaryManager);
        var harness = new TransientLlamaServerEvaluationHarness(supervisor,
            binaryManager,
            variantSelector,
            manifestProbe,
            launchPolicy,
            new TransientLlamaServerLauncher(binaryManager, variantSelector, launches, healthProbe, NullLogger<TransientLlamaServerLauncher>.Instance, TimeProvider.System),
            new NoOpGpuModelLoadAdmission(),
            ControlVectorFiles.RealGgufReader());
        await using (supervisor)
        {
            // 1. Baseline, vector and random arms, twice, under one lease.
            var request = new TransientLlamaServerEvaluationRequest
            {
                ModelFilePath = gguf,
                AdapterFilePath = null,
                ContextTokens = 2048,
                ReadinessTimeout = TimeSpan.FromMinutes(2),
                LaunchPolicy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1
            };
            var steered = request with
            {
                ControlVectorPath = vector
            };
            var random = request with
            {
                ControlVectorPath = randomVector
            };
            Task<ILlamaServerRuntimeMutationLease?>? competing = null;
            var competingCompletedDuringArms = false;

            // Each launch also reads a fixed amount (the runtime checks and the receipt), so the GGUF's share comes from comparing run sizes:
            // one arm = H + k, six arms = H + 6k, and the single-run path is the control that hashes twice.
            _ = await harness.RunArmsAsync([request], static (_, _, _) => Task.FromResult(true), deadline.Token);
            var oneArm = await MeasureBytesReadAsync(() => harness.RunArmsAsync([request], static (_, _, _) => Task.FromResult(true), deadline.Token));
            var singleRun = await MeasureBytesReadAsync(() => harness.RunAsync(request, static (_, _) => Task.CompletedTask, static (_, _) => Task.FromResult(true), deadline.Token));
            var bytesReadBefore = BytesRead();
            var watch = Stopwatch.StartNew();
            var memoryBefore = GpuMemoryUsed();
            var memoryDuring = new List<string>();
            var arms = await harness.RunArmsAsync([request, steered, random, request, steered, random],
                async (_, session, ct) =>
                {
                    competing ??= supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None);
                    competingCompletedDuringArms |= competing.IsCompleted;
                    memoryDuring.Add(GpuMemoryUsed());
                    return await CompleteAsync(http, session.BaseAddress, ct);
                },
                deadline.Token);
            watch.Stop();
            var bytesRead = BytesRead() - bytesReadBefore;
            var leaseAfterArms = await competing!.WaitAsync(TimeSpan.FromSeconds(10));
            await leaseAfterArms!.DisposeAsync();
            var modelBytes = new FileInfo(gguf).Length;
            evidence["arms"] = arms.Select(static (arm, index) => new
            {
                index,
                tokens = arm.Value.Tokens,
                text = arm.Value.Text,
                vector = arm.ControlVector,
                pid = arm.Teardown.ProcessId,
                exitObserved = arm.Teardown.ProcessExitObserved
            }).ToArray();
            evidence["launchArguments"] = launches.Specs.Select(static spec => spec.Arguments).ToArray();
            evidence["receipt"] = new
            {
                arms[0].Launch.ExecutableVersion,
                arms[0].Launch.ExecutableSha256,
                variant = arms[0].Launch.Variant.ToString(),
                arms[0].Launch.ManifestSha256,
                arms[0].Model.ModelSha256,
                arms[0].Model.ModelSizeBytes
            };
            evidence["lease"] = new
            {
                competingCompletedDuringArms,
                releasedAfterArms = leaseAfterArms is not null
            };
            var perLaunch = (bytesRead - oneArm) / 5.0;
            var ggufReadsInArms = (oneArm - perLaunch) / modelBytes;
            var ggufReadsInSingleRun = (singleRun - perLaunch) / modelBytes;
            evidence["hash"] = new
            {
                modelBytes,
                oneArmBytesRead = oneArm,
                sixArmsBytesRead = bytesRead,
                singleRunBytesRead = singleRun,
                perLaunch,
                ggufReadsInArms,
                ggufReadsInSingleRun
            };
            evidence["timing"] = new
            {
                sixArmsSeconds = watch.Elapsed.TotalSeconds
            };
            evidence["gpuMemory"] = new
            {
                before = memoryBefore,
                during = memoryDuring
            };
            AssertEx.False(competingCompletedDuringArms, "Nothing may take the runtime during the arms.");
            AssertEx.Equal(arms[0].Value.TokenKey, arms[3].Value.TokenKey, "The baseline must repeat.");
            AssertEx.Equal(arms[1].Value.TokenKey, arms[4].Value.TokenKey, "The vector arm must repeat.");
            AssertEx.Equal(arms[2].Value.TokenKey, arms[5].Value.TokenKey, "The random arm must repeat.");
            AssertEx.NotEqual(arms[0].Value.TokenKey, arms[1].Value.TokenKey, "The vector must change the output on this launch.");
            AssertEx.NotEqual(arms[0].Value.TokenKey, arms[2].Value.TokenKey, "The random vector must change the output on this launch.");
            AssertEx.True(ggufReadsInArms is > 0.8 and < 1.2, $"Arms must read the GGUF once; measured {ggufReadsInArms}.");
            AssertEx.True(ggufReadsInSingleRun is > 1.8 and < 2.2, $"The control must read the GGUF twice; measured {ggufReadsInSingleRun}.");
            AssertNothingLeft(gguf, launches);

            // 2 and 3. Everything llama-server would drop silently, refused with nothing started.
            var refusals = new Dictionary<string, object?>(StringComparer.Ordinal);
            var before = launches.Specs.Count;
            var badVectors = new Dictionary<string, TransientLlamaServerEvaluationRequest>(StringComparer.Ordinal)
            {
                ["wrong-size 512"] = request with
                {
                    ControlVectorPath = files.Vector("direction.15", 512)
                },
                ["9B-sized 4096 on the 0.8B"] = request with
                {
                    ControlVectorPath = files.Vector("direction.15", 4096)
                },
                ["0.8B vector on the 9B"] = request with
                {
                    ModelFilePath = otherGguf,
                    ControlVectorPath = vector
                },
                ["direction.24"] = request with
                {
                    ControlVectorPath = files.Vector("direction.24", 1024)
                },
                ["direction.99"] = request with
                {
                    ControlVectorPath = files.Vector("direction.99", 1024)
                },
                ["direction.0"] = request with
                {
                    ControlVectorPath = files.Vector("direction.0", 1024)
                },
                ["garbage"] = request with
                {
                    ControlVectorPath = files.BadVector("garbage", 1024)
                },
                ["missing"] = request with
                {
                    ControlVectorPath = files.BadVector("missing", 1024)
                }
            };
            foreach (var (name, bad) in badVectors)
            {
                var refusal = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => harness.RunArmsAsync([request, bad],
                    (_, session, ct) => CompleteAsync(http, session.BaseAddress, ct), deadline.Token));
                refusals[name] = refusal.Message;
            }

            evidence["refusals"] = refusals;
            evidence["launchesDuringRefusals"] = launches.Specs.Count - before;
            AssertEx.Equal(before, launches.Specs.Count, "A refused vector starts no process.");
            AssertNothingLeft(gguf, launches);

            // 4. A failing middle arm, then cancellation between arms.
            before = launches.Specs.Count;
            var failure = await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.RunArmsAsync([request, steered, random],
                async (arm, session, ct) =>
                {
                    var output = await CompleteAsync(http, session.BaseAddress, ct);
                    return arm == 1 ? throw new InvalidOperationException("Synthetic arm failure.") : output;
                },
                deadline.Token));
            var launchesInFailure = launches.Specs.Count - before;
            AssertNothingLeft(gguf, launches);
            await (await supervisor.AcquireRuntimeMutationLeaseOrNullAsync(deadline.Token))!.DisposeAsync();

            before = launches.Specs.Count;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            await AssertEx.ThrowsAsync<OperationCanceledException>(() => harness.RunArmsAsync([request, steered],
                async (_, session, ct) =>
                {
                    var output = await CompleteAsync(http, session.BaseAddress, ct);
                    await cancel.CancelAsync();
                    return output;
                },
                cancel.Token));
            var launchesInCancel = launches.Specs.Count - before;
            AssertNothingLeft(gguf, launches);
            await (await supervisor.AcquireRuntimeMutationLeaseOrNullAsync(deadline.Token))!.DisposeAsync();

            // Afterwards: the same unsteered launch repeats the baseline, and a normal chat launch serves with no vector.
            var again = await harness.RunArmsAsync([request], (_, session, ct) => CompleteAsync(http, session.BaseAddress, ct), deadline.Token);
            AssertEx.Equal(arms[0].Value.TokenKey, again[0].Value.TokenKey, "A failed or cancelled run must not change the next unsteered one.");

            var endpoint = await supervisor.EnsureRunningAsync("model-lab-live", ModelRole.Chat, deadline.Token);
            var chatSpec = launches.Specs[^1];
            var chat = await CompleteAsync(http, endpoint.BaseAddress, deadline.Token);
            evidence["afterwards"] = new
            {
                failure = failure.Message,
                launchesInFailure,
                launchesInCancel,
                unsteeredRepeatsBaseline = true,
                chatLaunchHasVector = chatSpec.Arguments.Any(static argument => argument.Contains("control-vector", StringComparison.Ordinal)),
                chatArguments = chatSpec.Arguments,
                chatText = chat.Text
            };
            AssertEx.Equal(expected: 2, launchesInFailure, "The arm after the failure must not start.");
            AssertEx.Equal(expected: 1, launchesInCancel, "The arm after the cancellation must not start.");
            AssertEx.False(chatSpec.Arguments.Any(static argument => argument.Contains("control-vector", StringComparison.Ordinal)));
            AssertEx.True(chat.Tokens.Length > 0);
        }

        AssertNothingLeft(gguf, launches);
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(evidence, Indented));
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable("XE_MODEL_LAB_LIVE_" + name);
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip.Test($"BLOCKED: opt-in live proof; XE_MODEL_LAB_LIVE_{name} is not set.");
        }

        return value;
    }

    private static async Task<Completion> CompleteAsync(HttpClient http, Uri baseAddress, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(new Uri(baseAddress, "/completion"), new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["prompt"] = Prompt,
            ["n_predict"] = 32,
            ["temperature"] = 0,
            ["seed"] = 1234,
            ["cache_prompt"] = false,
            ["return_tokens"] = true
        }, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var tokens = document.RootElement.GetProperty("tokens").EnumerateArray().Select(static token => token.GetInt32()).ToArray();
        return new Completion
        {
            Tokens = tokens,
            Text = document.RootElement.GetProperty("content").GetString() ?? string.Empty
        };
    }

    private static async Task<long> MeasureBytesReadAsync(Func<Task> run)
    {
        var before = BytesRead();
        await run();
        return BytesRead() - before;
    }

    private static long BytesRead() =>
        long.Parse(File.ReadLines("/proc/self/io").First(static line => line.StartsWith("rchar:", StringComparison.Ordinal))["rchar:".Length..].Trim(), CultureInfo.InvariantCulture);

    private static string GpuMemoryUsed()
    {
        using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader")
        {
            RedirectStandardOutput = true
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(10_000);
        return output;
    }

    private static void AssertNothingLeft(string gguf, RecordingLauncher launches)
    {
        var alive = launches.ProcessIds.Where(static pid => Directory.Exists($"/proc/{pid}")).ToArray();
        AssertEx.Empty(alive, "Every spawned process must be gone.");
        var byPath = Directory.GetDirectories("/proc")
                              .Where(static dir => int.TryParse(Path.GetFileName(dir), out _))
                              .Where(dir => ReadCommandLineOrEmpty(dir).Contains(gguf, StringComparison.Ordinal) && ReadCommandLineOrEmpty(dir).Contains("llama-server", StringComparison.Ordinal))
                              .ToArray();
        AssertEx.Empty(byPath, "No llama-server may still hold this model.");
        foreach (var port in launches.Specs.Select(static spec => spec.Port))
        {
            AssertEx.False(IsListening(port), $"Port {port} must be closed.");
        }
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string ReadCommandLineOrEmpty(string procDirectory)
    {
        try
        {
            return File.ReadAllText(Path.Combine(procDirectory, "cmdline"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private sealed class Completion
    {
        public required int[] Tokens { get; init; }

        public required string Text { get; init; }

        public string TokenKey => string.Join(' ', Tokens);
    }

    private sealed class RecordingLauncher : ILlamaServerProcessLauncher
    {
        private readonly ILlamaServerProcessLauncher _inner;
        private readonly ConcurrentQueue<int> _processIds = new();
        private readonly ConcurrentQueue<LlamaServerLaunchSpec> _specs = new();

        public RecordingLauncher(ILlamaServerProcessLauncher inner)
        {
            _inner = inner;
        }

        public IReadOnlyList<int> ProcessIds => [.. _processIds];

        public IReadOnlyList<LlamaServerLaunchSpec> Specs => [.. _specs];

        public IProcessTreeHandle Launch(LlamaServerLaunchSpec spec)
        {
            _specs.Enqueue(spec);
            var handle = _inner.Launch(spec);
            _processIds.Enqueue(handle.ProcessId);
            return handle;
        }
    }
}
