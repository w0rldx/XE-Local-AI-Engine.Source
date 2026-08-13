namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Default <see cref="IKnowledgeChunkEmbedder" />. Reuses the node-local embedding resolution path from
///     <c>EmbeddingPlaybookRetrievalRanker</c>: resolves the provider named by <see cref="KnowledgeBaseOptions.EmbeddingProviderName" />,
///     creates a generator for <see cref="KnowledgeBaseOptions.EmbeddingModelName" />, and generates in batches of at most
///     <see cref="KnowledgeBaseOptions.MaxEmbeddingBatchSize" />. Vectors are compared only within one model/dimension, so a
///     count or dimension mismatch, or any transport/model error, is surfaced as a content-free
///     <see cref="KnowledgeIngestionException" /> for the pipeline to record as <c>Failed</c>. No chunk text is ever logged.
/// </summary>
public sealed class KnowledgeChunkEmbedder : IKnowledgeChunkEmbedder
{
    // Fixed, content-free failure reasons (safe to persist + surface).
    //
    // The wording is deliberate. It previously said "Pull the configured embedding model (for example nomic-embed-text)",
    // which was wrong twice over on a default node: "pull" is Ollama vocabulary and Ollama is a disabled secondary
    // provider, and "nomic-embed-text" is an Ollama-style name that never appears anywhere in this app's UI. A user who
    // read it had no way to act on it (F-020, live eval 2026-07-31). It now names the in-app affordance that actually
    // resolves the failure. Keep it content-free — this string is persisted on the document row and surfaced verbatim.
    private const string EmbeddingUnavailableReason =
        "No embedding model is installed, so documents cannot be indexed. Use \"Download recommended embedding model\" "
        + "in Node Settings, or install an embedding GGUF from Models → Browse Hugging Face, then retry.";

    private const string EmbeddingIncompleteReason =
        "The embedding model returned an incomplete result. Ensure the configured embedding model is loaded and retry.";

    // A REACHABLE embedding server that REJECTED the request is a different failure from "no embedding model is
    // installed", and reporting it as the latter sends the user to install a model they already have. This was the live
    // symptom: llama-server rejected every full-size chunk (its physical batch defaulted to 512 tokens, below the chunk
    // budget) and the user was told to install an embedding GGUF that was installed, loaded, and serving on the GPU.
    // The provider layer translates a non-2xx into an HttpRequestException carrying the status, which is what separates
    // the two cases here. Content-free: the server's own diagnostic goes to the log, never into this persisted reason.
    private const string EmbeddingRejectedReason =
        "The embedding model is installed but rejected the request. Check the node logs for the server's response, then retry.";

    private readonly KnowledgeBaseOptions _options;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IEmbeddingModelResolver _embeddingModelResolver;
    private readonly IKnowledgeEmbeddingPrefixer _prefixer;

    public KnowledgeChunkEmbedder(ILocalModelProviderResolver providerResolver,
        IEmbeddingModelResolver embeddingModelResolver,
        IKnowledgeEmbeddingPrefixer prefixer,
        IOptions<KnowledgeBaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(embeddingModelResolver);
        ArgumentNullException.ThrowIfNull(prefixer);
        ArgumentNullException.ThrowIfNull(options);

        _providerResolver = providerResolver;
        _embeddingModelResolver = embeddingModelResolver;
        _prefixer = prefixer;
        _options = options.Value;
    }

    public async Task<KnowledgeEmbeddingResult> EmbedAsync(IReadOnlyList<string> chunkContents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunkContents);
        if (chunkContents.Count == 0)
        {
            // No provider round-trip for empty input; report the configured name as the resolved identity. The sole
            // caller (KnowledgeIngestionService) only reaches EmbedAsync with chunking.Chunks, and RunAsync marks a
            // zero-chunk document Failed before it ever calls EmbedAsync — so a document stamped via this branch can
            // never reach Indexed, and this placeholder name is never compared as a vector identity.
            return new KnowledgeEmbeddingResult([], _options.EmbeddingModelName, KnowledgeEmbeddingVectorPolicy.LegacyIdentity, Dimension: 0);
        }

        var provider = ResolveProvider();

        // Resolve the configured embedding name to a model actually installed on this provider (an Ollama-style default
        // maps to the installed nomic-embed GGUF on a llama.cpp node). Resolve ONCE and return the resolved name so the
        // ingestion lane can stamp the exact model that produced these vectors as the document row and chunk-vector scope
        // key. The search lane resolves the same way, so chunk vectors and query vectors are built by the identical model.
        var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false);
        var embeddingModelName = resolution.Name;
        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = embeddingModelName,
            ProviderName = _options.EmbeddingProviderName
        });

        var batchSize = Math.Max(1, _options.MaxEmbeddingBatchSize);
        var blobs = new List<byte[]>(chunkContents.Count);

        // The vector width is derived from the first embedding this run produces (not a static config constant), so any
        // embedding model's native dimension is honored. Every subsequent vector is checked against that first width: a
        // well-behaved model is dimension-stable, so a mismatch is a genuinely broken/mixed model and fails the document.
        var dimension = -1;
        string? vectorIdentity = null;

        for (var offset = 0; offset < chunkContents.Count; offset += batchSize)
        {
            var count = Math.Min(batchSize, chunkContents.Count - offset);
            var batch = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                // Prepend the document-intent prefix only on the text handed to the generator — never into stored content.
                batch.Add(_prefixer.ForDocument(chunkContents[offset + index]));
            }

            IReadOnlyList<Embedding<float>> generated;
            try
            {
                generated = await generator.GenerateAsync(batch, options: null, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (exception.StatusCode is not null)
            {
                // The server ANSWERED, with a non-2xx. It is reachable and the model is loaded, so the "install a model"
                // remediation is wrong here. Carry the exception so the ingestion service can log the server's own
                // diagnostic (see KnowledgeIngestionService's failure logging).
                throw new KnowledgeIngestionException(EmbeddingRejectedReason, exception);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaException or InvalidOperationException)
            {
                // Mirror the ranker's caught set: model not pulled / provider process down / transport error / unregistered
                // provider name. None of the exception's text is surfaced — only a fixed, content-free reason.
                throw new KnowledgeIngestionException(EmbeddingUnavailableReason, exception);
            }

            if (generated.Count != batch.Count)
            {
                throw new KnowledgeIngestionException(EmbeddingIncompleteReason);
            }

            foreach (var vector in generated.Select(embedding => embedding.Vector))
            {
                var transformed = KnowledgeEmbeddingVectorPolicy.Transform(resolution, vector, _options.EmbeddingVectorMode);
                if (dimension < 0)
                {
                    dimension = transformed.Dimension;
                    vectorIdentity = transformed.Identity;
                }
                else if (transformed.Dimension != dimension || !string.Equals(transformed.Identity, vectorIdentity, StringComparison.Ordinal))
                {
                    // Content-free reason (only integer widths) so it is safe to persist and surface to the operator.
                    throw new KnowledgeIngestionException(
                        $"The embedding model returned inconsistent vector dimensions (expected {dimension}, got {transformed.Dimension}). Reindex with a single embedding model.");
                }

                blobs.Add(KnowledgeEmbeddingVectorPolicy.ToBytes(transformed));
            }
        }

        return new KnowledgeEmbeddingResult(blobs, embeddingModelName, vectorIdentity!, dimension);
    }

    public async Task<int?> ResolveEmbeddingContextWindowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
            var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false);

            // A non-confident resolution is a bare fallback name, not an actually-installed model — its window is unknown.
            if (!resolution.IsConfident)
            {
                return null;
            }

            var installed = await provider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var descriptor = installed.FirstOrDefault(model =>
                string.Equals(model.ModelName, resolution.Name, StringComparison.OrdinalIgnoreCase));

            return descriptor?.MaxContextTokens is int window && window > 0 ? window : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaException or InvalidOperationException)
        {
            // Provider process down / transport error / unmapped provider. The window is simply unknown; chunking falls
            // back to the configured token budget. The subsequent embed step surfaces any genuine provider failure.
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A provider request TIMEOUT (TaskCanceledException) the caller did not trigger — treat as an unknown window,
            // exactly like the transport failures above. A genuine caller cancellation falls through and propagates.
            return null;
        }
    }

    public async Task<KnowledgeEmbeddingDescriptor?> ResolveExpectedVectorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
            var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken).ConfigureAwait(false);
            var identity = KnowledgeEmbeddingVectorPolicy.TryCreateExpectedIdentity(resolution, _options.EmbeddingVectorMode);
            return identity is null
                ? null
                : new KnowledgeEmbeddingDescriptor(resolution.Name, identity, KnowledgeEmbeddingVectorPolicy.MatryoshkaWidth);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaException or InvalidOperationException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private ILocalModelProvider ResolveProvider()
    {
        try
        {
            return _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
        }
        catch (InvalidOperationException exception)
        {
            throw new KnowledgeIngestionException(EmbeddingUnavailableReason, exception);
        }
    }
}
