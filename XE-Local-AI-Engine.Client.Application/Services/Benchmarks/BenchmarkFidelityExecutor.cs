namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public interface IBenchmarkFidelityExecutor
{
    Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken);
}

/// <summary>
///     Measures one run's quant fidelity: perplexity, and — when the project opted in — KL divergence against a base
///     model's logits. Deliberately parallel to <see cref="BenchmarkJudgeExecutor" />, with two differences that are
///     not incidental:
///     <list type="bullet">
///         <item>there is no llama-server and therefore no readiness probe, so there is no launch receipt to record;
///             what is stored instead is a REDUCED, explicitly-labelled evidence block, because presenting it as a
///             full receipt would be exactly the drift a display-only axis must not introduce;</item>
///         <item>the perplexity window is pinned to 512 while everything else about the run's placement is replayed —
///             the placement is what differs between the runs being compared, the window is what makes them
///             comparable at all.</item>
///     </list>
/// </summary>
public sealed class BenchmarkFidelityExecutor(
    IBenchmarkStore store,
    IBenchmarkRuntimeSnapshotFactory snapshots,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    IGgufModelStore ggufModels,
    ICapacityService capacity,
    ILlamaCppBinaryManager binaries,
    IBenchmarkPerplexityRunner perplexity,
    BenchmarkKldBaseCache cache,
    IOptions<BenchmarkKldCacheOptions> cacheOptions,
    IRuntimeEnvironmentFactsProvider environmentFacts,
    IBenchmarkCancellationRegistry cancellations,
    BenchmarkAdmissionRetry admissionRetry,
    ILogger<BenchmarkFidelityExecutor> logger) : IBenchmarkFidelityExecutor
{
    /// <summary>
    ///     Mirrors the quantizer's refusal shape: this runtime cannot do the thing, and the message names the two
    ///     ways out rather than leaving an operator with a missing-file error.
    /// </summary>
    internal const string PerplexityUnavailableMessage =
        "This llama.cpp runtime does not include llama-perplexity, so quant fidelity cannot be measured. "
        + "Rebuild the runtime from source, or switch to a prebuilt binary.";

    internal const string FingerprintChangedMessage = "The installed model changed after the benchmark was created.";
    internal const string CapacityRejectedMessage = "The fidelity measurement could not reserve enough local model capacity.";
    internal const string UnparseableOutputMessage = "llama-perplexity produced no final estimate. Its last output was:";
    private const string BaseWaitedTooLongMessage = "Another process is still measuring the base-model logits this run needs. It will be retried.";

    /// <summary>
    ///     Generous next to the real cost: 200 chunks of a 27B is about a minute of prompt evaluation on a 5090, and
    ///     a base-logit pass over a large-vocabulary model on a slower box is a multiple of that. The alternative to
    ///     waiting is killing a measurement that was going to succeed.
    /// </summary>
    private static readonly TimeSpan MeasurementTimeout = TimeSpan.FromHours(2);

    public async Task ExecuteAsync(BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Kind != BenchmarkWorkKind.Fidelity)
        {
            throw new ArgumentException("Fidelity executor received non-fidelity work.", nameof(work));
        }

        if (work.FidelityAttemptId is not { } attemptId)
        {
            throw new ArgumentException("Fidelity work must name the attempt it measures.", nameof(work));
        }

        using var registration = cancellations.Register(work.RunId, BenchmarkWorkKind.Fidelity, cancellationToken);
        var token = registration.Token;
        try
        {
            var attempt = await store.GetFidelityAttemptAsync(attemptId, token).ConfigureAwait(false)
                          ?? throw new BenchmarkExecutionException("The fidelity attempt is no longer available.");
            var command = await MeasureAsync(work, attempt, token).ConfigureAwait(false);
            _ = await store.MarkFidelitySucceededAsync(command, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _ = await store.MarkFidelityCancelledAsync(work.RunId, work.Version, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Benchmark fidelity work {RunId} failed.", work.RunId);

            // Fail CLOSED. A measurement that could not be taken is recorded as a failure with a reason, never as a
            // success carrying nulls — a null perplexity beside a real one reads as "this quant has no loss".
            _ = await store.MarkFidelityFailedAsync(work.RunId,
                    work.Version,
                    exception is BenchmarkExecutionException or BenchmarkSnapshotException or LlamaRuntimeException
                        ? exception.Message
                        : "The fidelity measurement failed. See local logs for details.",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<BenchmarkFidelitySuccessCommand> MeasureAsync(BenchmarkClaimedWork work,
        BenchmarkFidelityAttemptRecord attempt,
        CancellationToken token)
    {
        var snapshot = snapshots.Deserialize(work.Run.RuntimeSnapshotJson.Span);
        var project = await store.GetProjectAsync(work.Run.ProjectId, token).ConfigureAwait(false)
                      ?? throw new BenchmarkExecutionException("The benchmark project is no longer available.");
        var corpus = BenchmarkFidelityCorpus.Require();
        var chunks = BenchmarkFidelityPolicy.ClampChunks(project.FidelityChunks);

        await using var modelLease = await installedModels.AcquireAsync(snapshot.PrimaryModel.ModelName, token).ConfigureAwait(false);
        if (!BenchmarkSnapshotModelComparer.Matches(snapshot.PrimaryModel, modelLease.Snapshot))
        {
            throw new BenchmarkExecutionException(FingerprintChangedMessage);
        }

        var modelPath = await ggufModels.ResolveModelFilePathAsync(snapshot.PrimaryModel.ModelName, token).ConfigureAwait(false)
                        ?? throw new BenchmarkExecutionException("The model file for this run is no longer on disk.");

        // Host facts captured before anything is reserved or spawned, exactly as the judge does. Non-throwing.
        var environment = await environmentFacts.CaptureAsync(snapshot.PrimaryRuntime.Variant, token).ConfigureAwait(false);

        // Sized on the PINNED 512 window rather than the project's context: that is what this process will allocate.
        // No launch admission, and the same retry the judge uses — a fidelity item is dequeued by the same FIFO
        // consumer that just ran the primary, so it routinely arrives while that llama-server is handing VRAM back.
        var decision = await BenchmarkCapacityAdmission.AdmitAsync(capacity,
                                                           new CapacityRequest(snapshot.PrimaryModel.ModelName,
                                                               ModelRole.Chat,
                                                               BenchmarkFidelityPolicy.ContextTokens,
                                                               PublishLaunchAdmission: false,
                                                               snapshot.PrimaryRuntime.KvTypeK),
                                                           new BenchmarkAdmissionContext(work.RunId,
                                                               "fidelity",
                                                               BenchmarkFidelityPolicy.ContextTokens,
                                                               snapshot.PrimaryRuntime.KvTypeK ?? BenchmarkKvCacheType.F16,
                                                               CapacityRejectedMessage),
                                                           admissionRetry,
                                                           logger,
                                                           token)
                                                       .ConfigureAwait(false);
        using var reservation = decision.Reservation;

        var binary = await binaries.EnsureBinaryAsync(snapshot.PrimaryRuntime.Variant, token).ConfigureAwait(false);
        if (binary.PerplexityExecutablePath is not { } executable)
        {
            throw new BenchmarkExecutionException(PerplexityUnavailableMessage);
        }

        var kld = string.Equals(attempt.Kind, "kld", StringComparison.Ordinal)
            ? await PrepareKldAsync(project, corpus, chunks, executable, snapshot, token).ConfigureAwait(false)
            : null;

        var arguments = BuildArguments(modelPath, corpus.Path, chunks, snapshot.PrimaryRuntime, kld?.BaseFilePath, isBasePhase: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(MeasurementTimeout);
        var result = await perplexity.RunAsync(executable, arguments, timeout.Token).ConfigureAwait(false);

        var reading = BenchmarkPerplexityOutputParser.TryParsePerplexity(result.Output);
        if (result.ExitCode != 0 || reading is null)
        {
            throw new BenchmarkExecutionException($"{UnparseableOutputMessage} {BenchmarkPerplexityOutputParser.Tail(result.Output)}");
        }

        var divergence = kld is null ? null : BenchmarkPerplexityOutputParser.TryParseKld(result.Output);
        if (kld is not null && divergence is null)
        {
            throw new BenchmarkExecutionException($"{UnparseableOutputMessage} {BenchmarkPerplexityOutputParser.Tail(result.Output)}");
        }

        return new BenchmarkFidelitySuccessCommand(work.RunId,
            work.Version,
            attempt.Id,
            reading.Mean,
            reading.StandardError,
            chunks,
            BenchmarkFidelityPolicy.ContextTokens,
            corpus.CorpusId,
            divergence?.Mean,
            divergence?.P99,
            divergence?.TopTokenAgreement,
            kld?.BaseModelName,
            kld?.BaseFingerprint,
            kld?.Key.Digest,
            BuildReceipt(executable, snapshot, arguments, corpus, chunks, environment));
    }

    /// <summary>
    ///     Makes the base-logit file this run's KL divergence is measured against exist, and returns the key that
    ///     identifies it. The key — not the base model's fingerprint — is what a stored KLD figure is later gated on,
    ///     because the corpus, the chunk count and the format version all move without the fingerprint moving.
    /// </summary>
    private async Task<KldPreparation> PrepareKldAsync(BenchmarkProjectRecord project,
        BenchmarkFidelityCorpusFile corpus,
        int chunks,
        string executable,
        BenchmarkRuntimeSnapshotV1 snapshot,
        CancellationToken token)
    {
        if (project.FidelityKldBaseModelName is not { Length: > 0 } baseModelName || project.FidelityKldBaseFingerprint is not { Length: > 0 } baseFingerprint)
        {
            throw new BenchmarkExecutionException("KL divergence is enabled for this project but no base model is selected.");
        }

        var key = BenchmarkKldCacheKey.Create(baseFingerprint, corpus.Sha256, chunks);
        if (cache.TryResolveExisting(key) is { } existing)
        {
            return new KldPreparation(key, existing, baseModelName, baseFingerprint);
        }

        await using var baseLease = await installedModels.AcquireAsync(baseModelName, token).ConfigureAwait(false);
        if (!string.Equals(baseLease.Snapshot.ModelContentFingerprint, baseFingerprint, StringComparison.Ordinal))
        {
            throw new BenchmarkExecutionException("The selected KL-divergence base model changed since it was chosen for this project.");
        }

        var basePath = await ggufModels.ResolveModelFilePathAsync(baseModelName, token).ConfigureAwait(false)
                       ?? throw new BenchmarkExecutionException("The KL-divergence base model file is no longer on disk.");

        // The lease is the crash-safe half: DeleteOnClose means a killed writer's lock is released by the OS, so a
        // later run takes over instead of waiting on a lock nobody will release.
        using var lease = cache.TryAcquireLease(key);
        if (lease is null)
        {
            // Another writer holds it. Re-check the finished file once — it may have landed while we resolved paths —
            // and otherwise leave the item to be retried rather than writing a second multi-gigabyte copy.
            return cache.TryResolveExisting(key) is { } published
                ? new KldPreparation(key, published, baseModelName, baseFingerprint)
                : throw new BenchmarkExecutionException(BaseWaitedTooLongMessage);
        }

        cache.EnsureSpaceFor(BenchmarkFidelityPolicy.EstimateKldBytes(chunks, BenchmarkFidelityPolicy.DefaultVocabSize));
        var tempPath = cache.TempPathFor(key, Guid.NewGuid());
        try
        {
            var arguments = BuildArguments(basePath, corpus.Path, chunks, snapshot.PrimaryRuntime, tempPath, isBasePhase: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(MeasurementTimeout);
            var result = await perplexity.RunAsync(executable, arguments, timeout.Token).ConfigureAwait(false);
            if (result.ExitCode != 0 || BenchmarkPerplexityOutputParser.TryParsePerplexity(result.Output) is null)
            {
                throw new BenchmarkExecutionException($"{UnparseableOutputMessage} {BenchmarkPerplexityOutputParser.Tail(result.Output)}");
            }

            // Same-directory move, so it is atomic: a reader never observes a partial logit file, and a killed base
            // phase leaves a .tmp nobody will mistake for a measurement.
            cache.Publish(key, tempPath);
        }
        finally
        {
            BenchmarkKldBaseCache.DeleteBestEffort(tempPath);
        }

        _ = cache.Trim(cacheOptions.Value.KldCacheMaxBytes, await store.ListLiveFidelityDigestsAsync(token).ConfigureAwait(false));
        return new KldPreparation(key, cache.PathFor(key), baseModelName, baseFingerprint);
    }

    /// <summary>
    ///     The run's frozen placement replayed, with the window pinned. <c>-c 512</c> is the whole comparability
    ///     contract: perplexity means nothing across two different windows, and every published llama.cpp number uses
    ///     this one.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(string modelPath,
        string corpusPath,
        int chunks,
        BenchmarkLlamaRuntimeSnapshotV1 runtime,
        string? kldBasePath,
        bool isBasePhase)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var arguments = new List<string>
        {
            "-m",
            modelPath,
            "-f",
            corpusPath,
            "-c",
            BenchmarkFidelityPolicy.ContextTokens.ToString(CultureInfo.InvariantCulture),
            "--chunks",
            chunks.ToString(CultureInfo.InvariantCulture)
        };

        if (runtime.GpuLayers is { } gpuLayers)
        {
            arguments.Add("--n-gpu-layers");
            arguments.Add(gpuLayers.ToString(CultureInfo.InvariantCulture));
        }

        if (runtime.TensorSplit is { Length: > 0 } tensorSplit)
        {
            arguments.Add("--tensor-split");
            arguments.Add(tensorSplit);
        }

        if (runtime.OverrideTensor is { Length: > 0 } overrideTensor)
        {
            arguments.Add("--override-tensor");
            arguments.Add(overrideTensor);
        }

        // b10201's llama-perplexity accepts -ctk/-ctv, so a run measured under quantized KV is measured under the KV
        // it actually ran with. The attempt records the window and the chunk count beside the number either way.
        if (runtime.KvTypeK is { Length: > 0 } kvTypeK)
        {
            arguments.Add("--cache-type-k");
            arguments.Add(kvTypeK);
        }

        if (runtime.KvTypeV is { Length: > 0 } kvTypeV)
        {
            arguments.Add("--cache-type-v");
            arguments.Add(kvTypeV);
        }

        arguments.Add("--flash-attn");
        arguments.Add(runtime.FlashAttention ? "on" : "off");

        if (kldBasePath is { Length: > 0 })
        {
            if (!isBasePhase)
            {
                arguments.Add("--kl-divergence");
            }

            arguments.Add("--kl-divergence-base");
            arguments.Add(kldBasePath);
        }

        return arguments;
    }

    /// <summary>
    ///     A REDUCED evidence block, and labelled as one. llama-perplexity has no readiness probe, so there is no
    ///     launch receipt to be had; storing this under the receipt's own shape would let a UI present partial
    ///     evidence as complete evidence.
    /// </summary>
    private static ReadOnlyMemory<byte> BuildReceipt(string executable,
        BenchmarkRuntimeSnapshotV1 snapshot,
        IReadOnlyList<string> arguments,
        BenchmarkFidelityCorpusFile corpus,
        int chunks,
        RuntimeEnvironmentFactsV1? environment) =>
        Encoding.UTF8.GetBytes(BenchmarkCanonicalJson.Serialize(new
        {
            schemaVersion = 1,
            kind = "fidelity-evidence",
            executablePath = executable,
            executableSha256 = TryHashFile(executable),
            variant = snapshot.PrimaryRuntime.Variant,
            argv = arguments,
            corpusId = corpus.CorpusId,
            contextTokens = BenchmarkFidelityPolicy.ContextTokens,
            chunks,
            environmentFacts = environment
        }));

    private static string? TryHashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            // Evidence, not a precondition: a measurement that ran is still a measurement if its binary could not be
            // re-read afterwards.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record KldPreparation(BenchmarkKldCacheKey Key, string BaseFilePath, string BaseModelName, string BaseFingerprint);
}
