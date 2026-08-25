namespace XE_Local_AI_Engine.Tests.Benchmarks;

using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one thing the scripted tests cannot show: that the parser reads what the REAL binary writes, and that a
///     killed base phase publishes nothing. Both skip on a host with no llama.cpp runtime or no model to score, which
///     is every CI machine — the assertions here are for the box the measurement is actually taken on.
/// </summary>
public sealed class BenchmarkPerplexityRunnerLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_AgainstTheRealBinary_ProducesAnEstimateTheParserReads()
    {
        var (executable, model) = RequireRuntime();
        var corpus = BenchmarkFidelityCorpus.Require();
        var runtime = new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cpu, 4096, null, null, null, null, null, FlashAttention: false,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);
        var arguments = BenchmarkFidelityExecutor.BuildArguments(model, corpus.Path, chunks: 2, runtime, kldBasePath: null, isBasePhase: false);

        var result = await new BenchmarkPerplexityRunner().RunAsync(executable, arguments, CancellationToken.None);

        AssertEx.Equal(expected: 0, result.ExitCode, $"llama-perplexity failed: {BenchmarkPerplexityOutputParser.Tail(result.Output)}");
        var reading = AssertEx.NotNull(BenchmarkPerplexityOutputParser.TryParsePerplexity(result.Output),
            $"The parser must read the real binary's output; got: {BenchmarkPerplexityOutputParser.Tail(result.Output)}");
        AssertEx.True(reading.Mean is > 1.0 and < 1_000.0, $"A perplexity outside (1, 1000) is not a measurement; got {reading.Mean}.");
        AssertEx.True(reading.StandardError > 0, $"A standard error of zero over two chunks would mean the error was never parsed; got {reading.StandardError}.");
    }

    /// <summary>
    ///     The base phase writes to a temp path and lands by rename. Killed halfway, it must leave no <c>.logits</c> —
    ///     a partial logit file that looked finished would be read as a measurement by every later run.
    /// </summary>
    [Test]
    public async Task CancelledBasePhase_PublishesNoLogitsFile()
    {
        var (executable, model) = RequireRuntime();
        var corpus = BenchmarkFidelityCorpus.Require();
        var cache = new BenchmarkKldBaseCache(new UnlimitedFreeSpace(), _root);
        var key = BenchmarkKldCacheKey.Create("v1:" + new string('a', 64), corpus.Sha256, chunks: 200);
        Directory.CreateDirectory(cache.Root);
        var temp = cache.TempPathFor(key, Guid.NewGuid());
        var runtime = new BenchmarkLlamaRuntimeSnapshotV1(GpuVariant.Cpu, 4096, null, null, null, null, null, FlashAttention: false,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);
        var arguments = BenchmarkFidelityExecutor.BuildArguments(model, corpus.Path, chunks: 200, runtime, temp, isBasePhase: true);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            _ = await new BenchmarkPerplexityRunner().RunAsync(executable, arguments, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The expected path: the runner kills the tree rather than leaving a child writing to a file the caller
            // is about to delete.
        }
        finally
        {
            BenchmarkKldBaseCache.DeleteBestEffort(temp);
        }

        AssertEx.Null(cache.TryResolveExisting(key), "A killed base phase must publish nothing that resolves as a cached measurement.");
        AssertEx.Empty(Directory.EnumerateFiles(cache.Root, "*.logits"));
    }

    /// <summary>
    ///     Resolves this host's llama-perplexity and any GGUF to score. Both are the operator's, not the repo's, so
    ///     their absence is a skip rather than a failure.
    /// </summary>
    private static (string Executable, string Model) RequireRuntime()
    {
        var serverPath = Environment.GetEnvironmentVariable("XE_LLAMACPP_SERVER_PATH");
        var executable = serverPath is { Length: > 0 } ? LlamaCppToolBinaries.TryResolvePerplexityBesideServer(serverPath) : null;
        if (executable is null)
        {
            throw new SkipTestException("this host has no llama-perplexity beside XE_LLAMACPP_SERVER_PATH to measure with.");
        }

        // The smallest GGUF on a box is usually an embedding model or a vision projector, and llama-perplexity
        // asserts rather than refuses on those ("encoder requires n_ubatch >= n_tokens"). Filtered by name because
        // the alternative is reading n_vocab and the architecture out of the header for a test-only choice.
        string[] notScorable = ["projector", "embed", "rerank", "mmproj"];
        var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine", "models");
        var model = Directory.Exists(models)
            ? Directory.EnumerateFiles(models, "*.gguf")
                       .Where(path => !notScorable.Any(marker => Path.GetFileName(path).Contains(marker, StringComparison.OrdinalIgnoreCase)))
                       .OrderBy(static path => new FileInfo(path).Length)
                       .FirstOrDefault()
            : null;
        return model is null
            ? throw new SkipTestException("this host has no installed GGUF to score perplexity over.")
            : (executable, model);
    }

    private sealed class UnlimitedFreeSpace : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) => long.MaxValue;
    }
}
