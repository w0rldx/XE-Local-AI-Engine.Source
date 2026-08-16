namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Locks the runtime-capability gate to the resolved executable rather than a remembered llama.cpp tag. A successful
///     <c>--version</c>/<c>--help</c> probe is cached for an unchanged file identity, a changed binary invalidates it,
///     failures remain retryable, mandatory launch flags fail closed, and unsupported optional tuning is omitted.
/// </summary>
public sealed class LlamaServerCapabilityManifestTests
{
    private const string FullHelp = """
                                    -m, --model FNAME
                                    --host HOST
                                    --port PORT
                                    -c, --ctx-size N
                                    -ngl, --n-gpu-layers N
                                    --parallel N
                                    --no-warmup
                                    --fit [on|off]
                                    --metrics
                                    --jinja
                                    --cache-reuse N
                                    --cache-ram N
                                    -fa, --flash-attn [on|off|auto]
                                    -ctk, --cache-type-k TYPE
                                        allowed values: f32, f16, q8_0, q4_0
                                    -ctv, --cache-type-v TYPE
                                        allowed values: f32, f16, q8_0, q4_0
                                    -lv, --verbosity N
                                    --spec-type none,draft-simple,draft-mtp,ngram-cache
                                    --spec-draft-n-max N
                                    """;

    [Test]
    public void ParseHelp_IndexesAliasesAndCapabilityValues()
    {
        var parsed = LlamaServerCapabilityManifest.ParseHelp(FullHelp);

        AssertEx.True(parsed.Options.Contains("-m"));
        AssertEx.True(parsed.Options.Contains("--model"));
        AssertEx.True(parsed.Options.Contains("--cache-reuse"));
        AssertEx.True(parsed.SpeculativeModes.Contains("draft-mtp"));
        AssertEx.True(parsed.SpeculativeModes.Contains("ngram-cache"));
        AssertEx.True(parsed.CacheTypesK.Contains("q8_0"));
        AssertEx.True(parsed.CacheTypesV.Contains("q4_0"));
        AssertEx.True(parsed.FlashAttentionModes.Contains("on"));
        AssertEx.False(parsed.Options.Contains("--mmproj"));
    }

    [Test]
    public async Task GetManifest_UnchangedBinary_UsesOneVersionAndHelpProbe()
    {
        using var temp = new TempBinary();
        var runner = new FakeCommandRunner(SuccessfulResult);
        var probe = new LlamaServerCapabilityManifestProbe(runner, NullLogger<LlamaServerCapabilityManifestProbe>.Instance);
        var binary = temp.AsBinary();

        var first = await probe.GetManifestAsync(binary, CancellationToken.None);
        var second = await probe.GetManifestAsync(binary, CancellationToken.None);

        AssertEx.True(first.ProbeSucceeded);
        AssertEx.Equal(AssertEx.NotNull(first.ExecutableSha256), second.ExecutableSha256);

        // Lowercase hex, like every other digest in the tree: the freeze records this value as the INTENDED
        // executable and the receipt records a fresh digest of the running image, and the two are compared ordinally.
        AssertEx.Equal(temp.InitialSha256, first.ExecutableSha256);
        AssertEx.Equal("version: 10201 (b10201)", first.Version);
        AssertEx.Equal(2, runner.Calls.Count);
        AssertEx.Equal("--version", runner.Calls.ElementAt(0).Single());
        AssertEx.Equal("--help", runner.Calls.ElementAt(1).Single());
    }

    [Test]
    public async Task GetManifest_BinaryIdentityChanges_Reprobes()
    {
        using var temp = new TempBinary();
        var runner = new FakeCommandRunner(SuccessfulResult);
        var probe = new LlamaServerCapabilityManifestProbe(runner, NullLogger<LlamaServerCapabilityManifestProbe>.Instance);
        var binary = temp.AsBinary();

        _ = await probe.GetManifestAsync(binary, CancellationToken.None);
        temp.ReplaceContents("replacement binary with a different length");
        var changed = await probe.GetManifestAsync(binary, CancellationToken.None);

        AssertEx.True(changed.ProbeSucceeded);
        AssertEx.Equal(4, runner.Calls.Count);
        AssertEx.False(string.Equals(temp.InitialSha256, changed.ExecutableSha256, StringComparison.Ordinal));
    }

    [Test]
    public async Task GetManifest_SameLengthAndMtimeButDifferentHash_Reprobes()
    {
        using var temp = new TempBinary();
        var runner = new FakeCommandRunner(SuccessfulResult);
        var probe = new LlamaServerCapabilityManifestProbe(runner, NullLogger<LlamaServerCapabilityManifestProbe>.Instance);
        var binary = temp.AsBinary();
        var originalMtime = File.GetLastWriteTimeUtc(temp.Path);

        _ = await probe.GetManifestAsync(binary, CancellationToken.None);
        temp.ReplaceContents("changed binary", originalMtime);
        var changed = await probe.GetManifestAsync(binary, CancellationToken.None);

        AssertEx.True(changed.ProbeSucceeded);
        AssertEx.Equal(4, runner.Calls.Count);
        AssertEx.False(string.Equals(temp.InitialSha256, changed.ExecutableSha256, StringComparison.Ordinal));
    }

    [Test]
    public async Task GetManifest_BinaryChangesDuringProbe_DiscardsAndDoesNotCacheResult()
    {
        using var temp = new TempBinary();
        var changed = false;
        var runner = new FakeCommandRunner(arguments =>
        {
            if (!changed && arguments.Contains("--help", StringComparer.Ordinal))
            {
                temp.ReplaceContents("changed binary");
                changed = true;
            }

            return SuccessfulResult(arguments);
        });
        var probe = new LlamaServerCapabilityManifestProbe(runner, NullLogger<LlamaServerCapabilityManifestProbe>.Instance);

        var first = await probe.GetManifestAsync(temp.AsBinary(), CancellationToken.None);
        var second = await probe.GetManifestAsync(temp.AsBinary(), CancellationToken.None);

        AssertEx.False(first.ProbeSucceeded);
        AssertEx.True(second.ProbeSucceeded);
        AssertEx.Equal(4, runner.Calls.Count);
    }

    [Test]
    public async Task GetManifest_FailedHelpProbe_IsNotCached()
    {
        using var temp = new TempBinary();
        var call = 0;
        var runner = new FakeCommandRunner(_ =>
        {
            call++;
            return call % 2 == 1
                ? VersionResult()
                : null;
        });
        var probe = new LlamaServerCapabilityManifestProbe(runner, NullLogger<LlamaServerCapabilityManifestProbe>.Instance);
        var binary = temp.AsBinary();

        var first = await probe.GetManifestAsync(binary, CancellationToken.None);
        var second = await probe.GetManifestAsync(binary, CancellationToken.None);

        AssertEx.False(first.ProbeSucceeded);
        AssertEx.False(second.ProbeSucceeded);
        AssertEx.Equal(4, runner.Calls.Count);
    }

    [Test]
    public async Task GetManifest_ConcurrentColdCallers_ShareOneProbe()
    {
        using var temp = new TempBinary();
        var runner = new GatedCommandRunner();
        var probe = new LlamaServerCapabilityManifestProbe(runner, NullLogger<LlamaServerCapabilityManifestProbe>.Instance);

        var first = probe.GetManifestAsync(temp.AsBinary(), CancellationToken.None);
        await runner.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = probe.GetManifestAsync(temp.AsBinary(), CancellationToken.None);
        runner.Release.TrySetResult();
        var manifests = await Task.WhenAll(first, second);

        AssertEx.True(manifests.All(static manifest => manifest.ProbeSucceeded));
        AssertEx.Equal(2, runner.Calls.Count);
    }

    [Test]
    public async Task GetManifest_CancelledOwner_DoesNotCacheFailureOrPoisonNextCaller()
    {
        using var temp = new TempBinary();
        var runner = new CancelOnceCommandRunner();
        var probe = new LlamaServerCapabilityManifestProbe(runner, NullLogger<LlamaServerCapabilityManifestProbe>.Instance);
        using var cts = new CancellationTokenSource();
        var cancelledProbe = probe.GetManifestAsync(temp.AsBinary(), cts.Token);
        await runner.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => cancelledProbe);
        var recovered = await probe.GetManifestAsync(temp.AsBinary(), CancellationToken.None);

        AssertEx.True(recovered.ProbeSucceeded);
        AssertEx.Equal(3, runner.Calls.Count);
    }

    [Test]
    public void Apply_MissingMandatorySafetyFlag_RejectsRuntime()
    {
        var manifest = ManifestFromHelp(FullHelp.Replace("--cache-ram N", string.Empty, StringComparison.Ordinal));
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "-c", "4096", "--jinja", "--cache-ram", "512"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);

        AssertEx.False(decision.IsCompatible);
        AssertEx.Contains(decision.SanitizedError!, "--cache-ram");
    }

    [Test]
    public void Apply_MissingOptionalTuning_RemovesItButKeepsMandatoryArguments()
    {
        var helpWithoutOptional = FullHelp
                                  .Replace("--metrics", string.Empty, StringComparison.Ordinal)
                                  .Replace("--cache-reuse N", string.Empty, StringComparison.Ordinal);
        var manifest = ManifestFromHelp(helpWithoutOptional);
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "--metrics", "-c", "4096", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0",
            "--jinja", "--cache-reuse", "256", "--cache-ram", "512"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);

        AssertEx.True(decision.IsCompatible);
        AssertEx.False(decision.Spec.Arguments.Contains("--metrics"));
        AssertEx.False(decision.Spec.Arguments.Contains("--cache-reuse"));
        AssertEx.Contains(decision.Spec.Arguments, "-fa");
        AssertEx.Contains(decision.Spec.Arguments, "-ctk");
        AssertEx.Contains(decision.Spec.Arguments, "-ctv");
        AssertEx.Contains(decision.Spec.Arguments, "--no-warmup");
        AssertEx.Contains(decision.Spec.Arguments, "--cache-ram");
        AssertEx.Equal(2, decision.OmittedOptions.Count);
    }

    [Test]
    public void Apply_PartialKvCapability_RequestsExplicitSafeCandidateWithoutMutatingSpec()
    {
        var manifest = ManifestFromHelp(FullHelp.Replace("-ctv, --cache-type-v TYPE", string.Empty, StringComparison.Ordinal));
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "-c", "4096", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0",
            "--jinja", "--cache-ram", "512"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);

        AssertEx.False(decision.IsCompatible);
        AssertEx.True(decision.CanTrySafeFallback);
        AssertEx.Contains(decision.Spec.Arguments, "-fa");
        AssertEx.Contains(decision.Spec.Arguments, "-ctk");
        AssertEx.Contains(decision.Spec.Arguments, "-ctv");
        AssertEx.Equal(0, decision.OmittedOptions.Count);
    }

    [Test]
    public void Apply_LongAliasOnly_DoesNotApproveUnsupportedEmittedShortOption()
    {
        var manifest = ManifestFromHelp(FullHelp.Replace("-m, --model", "--model", StringComparison.Ordinal));
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "-c", "4096", "--jinja", "--cache-ram", "512"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);

        AssertEx.False(decision.IsCompatible);
        AssertEx.False(decision.CanTrySafeFallback);
        AssertEx.Contains(decision.SanitizedError!, "-m");
    }

    [Test]
    public void Apply_MandatoryOptionMentionedOnlyInProse_DoesNotCountAsSupported()
    {
        var help = FullHelp.Replace("--no-warmup", "", StringComparison.Ordinal)
                   + "\n--warmup       use warmup; the removed --no-warmup spelling is mentioned only here";
        var manifest = ManifestFromHelp(help);
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "-c", "4096", "--jinja", "--cache-ram", "512"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);

        AssertEx.False(decision.IsCompatible);
        AssertEx.Contains(decision.SanitizedError!, "--no-warmup");
    }

    [Test]
    public void Apply_LongFlashAliasOnly_DoesNotApproveUnsupportedEmittedShortOption()
    {
        var manifest = ManifestFromHelp(FullHelp.Replace("-fa, --flash-attn", "--flash-attn", StringComparison.Ordinal));
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "-c", "4096", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0",
            "--jinja", "--cache-ram", "512"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);

        AssertEx.False(decision.IsCompatible);
        AssertEx.True(decision.CanTrySafeFallback);
    }

    [Test]
    public void Apply_BenchmarkRequiresMetrics_RejectsRuntimeWithoutMetrics()
    {
        var manifest = ManifestFromHelp(FullHelp.Replace("--metrics", string.Empty, StringComparison.Ordinal));
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "--metrics", "-c", "4096", "--jinja", "--cache-ram", "512"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: true);

        AssertEx.False(decision.IsCompatible);
        AssertEx.Contains(decision.SanitizedError!, "--metrics");
    }

    [Test]
    public void Apply_UnsupportedSpeculativeMode_RejectsBeforeSpawn()
    {
        var manifest = ManifestFromHelp(FullHelp.Replace(",draft-mtp", string.Empty, StringComparison.Ordinal));
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "-c", "4096", "--jinja", "--cache-ram", "512", "--spec-type", "draft-mtp",
            "--spec-draft-n-max", "3"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);

        AssertEx.False(decision.IsCompatible);
        AssertEx.Contains(decision.SanitizedError!, "draft-mtp");
    }

    [Test]
    public void Apply_SpeculativeModeMentionedOnlyInProse_DoesNotCountAsSupported()
    {
        var help = FullHelp.Replace("--spec-type none,draft-simple,draft-mtp,ngram-cache",
            "--spec-type none,draft-simple\n  draft-mtp is unsupported by this build",
            StringComparison.Ordinal);
        var manifest = ManifestFromHelp(help);
        var spec = ChatSpec([
            "-m", "/models/model.gguf", "--host", "127.0.0.1", "--port", "12345", "--parallel", "1",
            "--no-warmup", "-c", "4096", "--jinja", "--cache-ram", "512", "--spec-type", "draft-mtp",
            "--spec-draft-n-max", "3"
        ]);

        var decision = LlamaServerCapabilityGate.Apply(spec, manifest, requireMetrics: false);

        AssertEx.False(decision.IsCompatible);
        AssertEx.Contains(decision.SanitizedError!, "draft-mtp");
    }

    private static LlamaCommandResult? SuccessfulResult(IReadOnlyList<string> arguments)
    {
        return arguments.Contains("--version", StringComparer.Ordinal) ? VersionResult() : HelpResult();
    }

    private static LlamaCommandResult VersionResult()
    {
        return new LlamaCommandResult(0, "version: 10201 (b10201)\n", string.Empty);
    }

    private static LlamaCommandResult HelpResult()
    {
        return new LlamaCommandResult(0, FullHelp, string.Empty);
    }

    private static LlamaServerCapabilityManifest ManifestFromHelp(string help)
    {
        return LlamaServerCapabilityManifest.FromSuccessfulProbe(new LlamaBinary("/fake/bin/llama-server", "b10201", GpuVariant.Cuda,
                IsPinnedFallback: true),
            executableLengthBytes: 123,
            executableLastWriteUtc: DateTimeOffset.UnixEpoch,
            executableSha256: new string('a', 64),
            version: "version: 10201 (b10201)",
            help);
    }

    private static LlamaServerLaunchSpec ChatSpec(IReadOnlyList<string> arguments)
    {
        return new LlamaServerLaunchSpec("model", ModelRole.Chat, "/fake/bin/llama-server", arguments, 12345, "/fake/bin");
    }

    private sealed class FakeCommandRunner(Func<IReadOnlyList<string>, LlamaCommandResult?> result) : ILlamaCommandProcessRunner
    {
        public ConcurrentQueue<IReadOnlyList<string>> Calls { get; } = new();

        public Task<LlamaCommandResult?> RunAsync(string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Calls.Enqueue([.. arguments]);
            return Task.FromResult(result(arguments));
        }
    }

    private sealed class GatedCommandRunner : ILlamaCommandProcessRunner
    {
        public ConcurrentQueue<IReadOnlyList<string>> Calls { get; } = new();

        public TaskCompletionSource FirstCallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlamaCommandResult?> RunAsync(string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Calls.Enqueue([.. arguments]);
            if (Calls.Count == 1)
            {
                FirstCallEntered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }

            return SuccessfulResult(arguments);
        }
    }

    private sealed class CancelOnceCommandRunner : ILlamaCommandProcessRunner
    {
        private int _calls;

        public ConcurrentQueue<IReadOnlyList<string>> Calls { get; } = new();

        public TaskCompletionSource FirstCallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlamaCommandResult?> RunAsync(string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Calls.Enqueue([.. arguments]);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return SuccessfulResult(arguments);
        }
    }

    private sealed class TempBinary : IDisposable
    {
        private readonly string _directory;

        public TempBinary()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xe-capability-manifest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "llama-server");
            File.WriteAllText(Path, "initial binary");
            InitialSha256 = ComputeSha256(Path);
        }

        public string InitialSha256 { get; }

        public string Path { get; }

        public LlamaBinary AsBinary()
        {
            return new LlamaBinary(Path, "b10201", GpuVariant.Cuda, IsPinnedFallback: true);
        }

        public void ReplaceContents(string contents, DateTime? lastWriteUtc = null)
        {
            File.WriteAllText(Path, contents);
            File.SetLastWriteTimeUtc(Path, lastWriteUtc ?? DateTime.UtcNow.AddMinutes(1));
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }

        private static string ComputeSha256(string path)
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
    }
}
