namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     One real <c>llama-server</c> child the live eval launched, for one GGUF in one role (pattern:
///     <c>LlamaGrammarLiveSmokeTests</c>). Disposing tree-kills it on every path, so a failed assert never leaks VRAM.
/// </summary>
internal sealed class LiveLlamaServer : IAsyncDisposable
{
    private const int StderrTailLines = 60;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _output;

    private LiveLlamaServer(Process process, ConcurrentQueue<string> output, int port, int contextTokens)
    {
        _process = process;
        _output = output;
        Port = port;
        ContextTokens = contextTokens;
    }

    public int Port { get; }

    /// <summary>The <c>-c</c> the server was launched with (the product's role context, capped at the train context).</summary>
    public int ContextTokens { get; }

    /// <summary>Cold start: process spawn until the first <c>/health</c> 200.</summary>
    public TimeSpan SpawnToReady { get; private set; }

    /// <summary>Device memory used after readiness minus before spawn, box-wide (nvidia-smi), or null when unreadable.</summary>
    public double? VramDeltaMiB { get; private set; }

    /// <summary>Resident set of the server process after readiness (<c>/proc/pid/status</c> VmRSS), or null when unreadable.</summary>
    public double? RssMiB { get; private set; }

    /// <summary>The OpenAI-compatible base the product clients expect (<c>http://127.0.0.1:port/v1</c>, no trailing slash).</summary>
    public Uri BaseAddress => new($"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/v1");

    /// <summary>The unique <c>--alias</c> this launch passed, the only model id its <c>/v1/models</c> may list.</summary>
    public required string Alias { get; init; }

    /// <summary>The last server output lines, for a failure report.</summary>
    public string OutputTail => string.Join('\n', _output);

    /// <summary>
    ///     The launch shape the PRODUCT gives this role, via <see cref="LlamaServerLaunchProjection.From" /> over a plan
    ///     built like <c>LlamaServerLaunchPolicy.ResolveAsync</c>: GPU = <c>--fit on</c>, metrics, KV quant; CPU = threads.
    /// </summary>
    public static LlamaServerLaunchProjection ProductProjection(ModelRole role, int gpuLayers, int contextTokens)
    {
        var policy = new LlamaServerLaunchPolicyOptions();
        var variant = gpuLayers > 0 ? GpuVariant.Cuda : GpuVariant.Cpu;
        var (threads, threadsBatch) = variant == GpuVariant.Cpu ? CpuThreads(policy) : (null, null);
        var plan = new LlamaServerLaunchPlan(contextTokens,
            UseKvCacheQuantization: variant != GpuVariant.Cpu && policy.EnableGpuKvCacheQuantization,
            policy.KvCacheType,
            threads,
            threadsBatch);
        var supervisor = new LlamaServerSupervisorOptions();
        return LlamaServerLaunchProjection.From(variant,
            ResolvedLaunchArguments.Explore(),
            plan,
            role,
            role == ModelRole.Chat ? supervisor.ChatCacheReuse : 0,
            role == ModelRole.Chat ? supervisor.ChatCacheRamMiB : 0);
    }

    /// <summary>
    ///     Renders <see cref="ProductProjection" /> in <c>LlamaServerLaunchArgumentComposer.BuildLaunchSpec</c> order (that
    ///     composer is internal), plus three additions outside the projection; see remarks.
    /// </summary>
    /// <remarks>
    ///     An explicit <c>-ngl</c>: the product leaves placement to <c>--fit</c>, which keeps an explicit value.
    ///     <c>--device none</c> on the CPU pass: <c>-ngl 0</c> alone still offloads batch matmuls on a CUDA build.
    ///     A unique <c>--alias</c> for the foreign-server guard: the product clients send no model id the single-model
    ///     server routes on (rerank sends none, embeddings send the installed name, which the server ignores).
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(ModelRole role, string modelPath, int port, string alias, int gpuLayers, int contextTokens)
    {
        var projection = ProductProjection(role, gpuLayers, contextTokens);
        var args = new List<string>
        {
            "-m", modelPath,
            "--alias", alias,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--parallel", projection.Parallel.ToString(CultureInfo.InvariantCulture),
            "--no-warmup"
        };

        if (projection.Metrics)
        {
            args.Add("--metrics");
        }

        if (projection.AutoFit)
        {
            args.AddRange(["--fit", "on"]);
        }

        if (projection.ContextTokens is { } context)
        {
            args.AddRange(["-c", context.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-ngl", gpuLayers.ToString(CultureInfo.InvariantCulture)]);
        if (gpuLayers == 0)
        {
            args.AddRange(["--device", "none"]);
        }

        if (projection is { KvCacheTypeK: { } typeK, KvCacheTypeV: { } typeV })
        {
            args.AddRange(projection.AutoFit
                ? ["-fa", "on", "-ctk", typeK, "-ctv", typeV]
                : ["-ctk", typeK, "-ctv", typeV, "--flash-attn", "on"]);
        }

        if (projection.Threads is { } threads)
        {
            args.AddRange(["-t", threads.ToString(CultureInfo.InvariantCulture)]);
        }

        if (projection.ThreadsBatch is { } threadsBatch)
        {
            args.AddRange(["-tb", threadsBatch.ToString(CultureInfo.InvariantCulture)]);
        }

        if (role == ModelRole.Chat)
        {
            args.Add("--jinja");
            if (projection.CacheReuse is { } reuse)
            {
                args.AddRange(["--cache-reuse", reuse.ToString(CultureInfo.InvariantCulture)]);
            }

            args.AddRange(["--cache-ram", projection.CacheRamMiB.ToString(CultureInfo.InvariantCulture)]);
            return args;
        }

        args.AddRange(role == ModelRole.Embedding ? ["--embeddings", "--pooling", "mean"] : ["--rerank", "--pooling", "rank"]);
        if (projection is { BatchSize: { } batch, UbatchSize: { } ubatch })
        {
            args.AddRange(["-b", batch.ToString(CultureInfo.InvariantCulture), "-ub", ubatch.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["--cache-ram", "0"]);
        return args;
    }

    /// <summary>
    ///     The pooled-role context the product launches with: the role's policy context capped at the model's train context
    ///     minus <see cref="LlamaServerLaunchPolicyOptions.ContextSafetyMarginTokens" /> and aligned down, mirroring
    ///     <c>ProcessContextAllocationResolver.CapAndAlign</c>.
    /// </summary>
    public static int PooledContextTokens(ModelRole role, long? trainContextTokens)
    {
        var policy = new LlamaServerLaunchPolicyOptions();
        var requested = role == ModelRole.Embedding ? policy.EmbeddingContextTokens : policy.RerankerContextTokens;
        var capped = trainContextTokens is > 0
            ? (int)Math.Min(requested, Math.Max(1, trainContextTokens.Value - policy.ContextSafetyMarginTokens))
            : requested;
        return capped < LlamaServerLaunchPolicyOptions.ContextAlignmentTokens
            ? Math.Max(1, capped)
            : capped / LlamaServerLaunchPolicyOptions.ContextAlignmentTokens * LlamaServerLaunchPolicyOptions.ContextAlignmentTokens;
    }

    public static async Task<LiveLlamaServer> StartAsync(string serverPath,
        string modelPath,
        ModelRole role,
        int gpuLayers,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken,
        int? chatContextTokens = null)
    {
        var contextTokens = role == ModelRole.Chat
            ? chatContextTokens ?? new LlamaServerLaunchPolicyOptions().ChatContextTokens
            : PooledContextTokens(role, await ReadTrainContextAsync(modelPath, cancellationToken));
        var port = ReserveLoopbackPort();
        var alias = $"xe-eval-{Guid.NewGuid():N}-{port.ToString(CultureInfo.InvariantCulture)}";
        var startInfo = new ProcessStartInfo(serverPath)
        {
            // The binary's own directory: a relocated build resolves its .so siblings through an $ORIGIN RUNPATH.
            WorkingDirectory = Path.GetDirectoryName(serverPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildArguments(role, modelPath, port, alias, gpuLayers, contextTokens))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var vramBefore = await ReadUsedVramMiBAsync(cancellationToken);
        var output = new ConcurrentQueue<string>();
        var started = Stopwatch.GetTimestamp();
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new LiveInfraException($"Could not start llama-server at '{serverPath}'.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new LiveInfraException($"Could not start llama-server at '{serverPath}': {exception.Message}", exception);
        }

        process.OutputDataReceived += (_, args) => Enqueue(output, args.Data);
        process.ErrorDataReceived += (_, args) => Enqueue(output, args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var server = new LiveLlamaServer(process, output, port, contextTokens)
        {
            Alias = alias
        };
        try
        {
            await WaitForHealthAsync(server, readyTimeout, cancellationToken);
            server.SpawnToReady = Stopwatch.GetElapsedTime(started);
            await server.RequireServesAsync(cancellationToken);
            var vramAfter = await ReadUsedVramMiBAsync(cancellationToken);
            server.VramDeltaMiB = vramBefore is { } before && vramAfter is { } after ? after - before : null;
            server.RssMiB = ReadRssMiB(process.Id);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }

        return server;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);

                // real-timer: bounds the wait for a killed child process to be reaped; there is no event to gate on.
                using var exitWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _process.WaitForExitAsync(exitWait.Token);
            }
        }
        catch (InvalidOperationException)
        {
            // Already reaped; nothing left to kill.
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>A loopback port nothing listens on, for the dead-endpoint negative control.</summary>
    public static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Box-wide used device memory in MiB (first GPU), or null without nvidia-smi.</summary>
    public static async Task<double?> ReadUsedVramMiBAsync(CancellationToken cancellationToken)
    {
        var raw = await RetrievalEvalLiveReportWriter.RunToolAsync("nvidia-smi", ["--query-gpu=memory.used", "--format=csv,noheader,nounits"], cancellationToken);
        var first = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var mib) ? mib : null;
    }

    // The product's own GGUF header reader (Providers.HuggingFace), reached through its public registration; the models
    // directory points at an unused scratch path because only the header reader is resolved, never the store.
    private static async Task<long?> ReadTrainContextAsync(string modelPath, CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                [$"{HuggingFaceOptions.SectionName}:{nameof(HuggingFaceOptions.ModelsDirectory)}"] =
                                    Path.Combine(Path.GetTempPath(), "xe-retrieval-eval-unused-models")
                            })
                            .Build();
        var services = new ServiceCollection()
                       .AddLogging()
                       .AddSingleton(TimeProvider.System)
                       .AddHuggingFaceGgufStore(configuration)
                       .AddGgufMetadataReader();
        await using var provider = services.BuildServiceProvider();
        var metadata = await provider.GetRequiredService<IGgufMetadataReader>().ReadMetadataAsync(modelPath, cancellationToken);
        return metadata.ContextLength;
    }

    private static double? ReadRssMiB(int processId)
    {
        var status = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/status";
        var line = File.Exists(status) ? File.ReadLines(status).FirstOrDefault(static candidate => candidate.StartsWith("VmRSS:", StringComparison.Ordinal)) : null;
        var kilobytes = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        return double.TryParse(kilobytes, NumberStyles.Float, CultureInfo.InvariantCulture, out var kb) ? kb / 1024d : null;
    }

    private static async Task WaitForHealthAsync(LiveLlamaServer server, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var health = new Uri($"http://127.0.0.1:{server.Port.ToString(CultureInfo.InvariantCulture)}/health");
        var deadline = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            server.RequireAlive();
            try
            {
                using var response = await http.GetAsync(health, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Probe timed out; the model is still loading.
            }

            // real-timer: polls a real spawned llama-server loading a model; /health is its only readiness signal.
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new LiveInfraException(
            $"llama-server did not report healthy within {budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s.\n{server.OutputTail}");
    }

    private void RequireAlive()
    {
        if (_process.HasExited)
        {
            throw new LiveInfraException(
                $"llama-server exited with code {_process.ExitCode.ToString(CultureInfo.InvariantCulture)} before or while becoming healthy.\n{OutputTail}");
        }
    }

    /// <summary>True when a <c>/v1/models</c> body lists exactly one model and its id is <paramref name="alias" />.</summary>
    public static bool ListsOnlyAlias(string modelsBody, string alias)
    {
        try
        {
            return JsonNode.Parse(modelsBody)?["data"] is JsonArray { Count: 1 } data
                   && string.Equals(data[0]?["id"]?.GetValue<string>(), alias, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // Foreign-server guard: the reserved port is free before llama-server binds it and checkouts share one model store,
    // so health and GGUF name prove nothing. Our process must be alive and /v1/models list only the per-launch alias.
    private async Task RequireServesAsync(CancellationToken cancellationToken)
    {
        RequireAlive();
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        var body = await http.GetStringAsync(new Uri(BaseAddress.AbsoluteUri + "/models"), cancellationToken);
        if (!ListsOnlyAlias(body, Alias))
        {
            throw new LiveInfraException($"The server on port {Port.ToString(CultureInfo.InvariantCulture)} does not list only this launch's alias '{Alias}': {body}");
        }
    }

    private static (int? Threads, int? ThreadsBatch) CpuThreads(LlamaServerLaunchPolicyOptions policy)
    {
        // Mirrors LlamaServerLaunchPolicy.ResolveCpuThreads (internal): physical = logical / 2 under assumed SMT, generation
        // threads = physical minus the host reserve, prompt-batch threads = physical; explicit overrides win.
        if (!policy.EnableCpuThreadPolicy)
        {
            return (null, null);
        }

        var logical = Environment.ProcessorCount;
        var physical = Math.Max(policy.AssumeSimultaneousMultithreading && logical >= 2 ? logical / 2 : logical, 1);
        var threads = policy.CpuThreadCount is > 0 ? policy.CpuThreadCount.Value : Math.Max(physical - policy.CpuThreadReserve, 1);
        var threadsBatch = policy.CpuThreadsBatchCount is > 0 ? policy.CpuThreadsBatchCount.Value : physical;
        return (threads, threadsBatch);
    }

    private static void Enqueue(ConcurrentQueue<string> log, string? line)
    {
        if (line is null)
        {
            return;
        }

        log.Enqueue(line);
        while (log.Count > StderrTailLines)
        {
            _ = log.TryDequeue(out _);
        }
    }
}

/// <summary>
///     The eval's infrastructure failed, not the product: a llama-server that would not start, never became healthy, or
///     turned out to be someone else's. The report records it as an <c>infra:</c> abort, which the runner maps to exit 5.
/// </summary>
internal sealed class LiveInfraException : InvalidOperationException
{
    public LiveInfraException(string message)
        : base(message)
    {
    }

    public LiveInfraException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
