namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     Default <see cref="IKnowledgeChunkEmbedder" />: resolves the provider named by
///     <see cref="KnowledgeBaseOptions.EmbeddingProviderName" /> and creates a generator for
///     <see cref="KnowledgeBaseOptions.EmbeddingModelName" />.
/// </summary>
/// <remarks>
///     Reuses the node-local embedding resolution path from <c>EmbeddingPlaybookRetrievalRanker</c> and generates in
///     batches of at most <see cref="KnowledgeBaseOptions.MaxEmbeddingBatchSize" />. Vectors are compared only within one
///     model and dimension, so a count or dimension mismatch, or any transport/model error, surfaces as a content-free
///     <see cref="KnowledgeIngestionException" /> for the pipeline to record as <c>Failed</c>. No chunk text is ever
///     logged.
/// </remarks>
public sealed class KnowledgeChunkEmbedder : IKnowledgeChunkEmbedder
{
    // Fixed, content-free failure reasons: each is persisted on the document row and surfaced verbatim, so it names the
    // in-app affordance that resolves it — never Ollama vocabulary ("pull") or an Ollama-style name the UI never shows.
    private const string EmbeddingUnavailableReason =
        "No embedding model is installed, so documents cannot be indexed. Use \"Download recommended embedding model\" "
        + "in Node Settings, or install an embedding GGUF from Models → Browse Hugging Face, then retry.";

    private const string EmbeddingIncompleteReason =
        "The embedding model returned an incomplete result. Ensure the configured embedding model is loaded and retry.";

    // A REACHABLE server that REJECTED the request is a different failure from "no embedding model is installed", and
    // reporting it as the latter sends the user to install a model they have; the non-2xx status separates the two cases.
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
            // No provider round-trip for empty input; report the configured name as the resolved identity. RunAsync marks
            // a zero-chunk document Failed first, so a document stamped here never reaches Indexed or a vector comparison.
            return new KnowledgeEmbeddingResult
            {
                Vectors = [],
                ResolvedModel = _options.EmbeddingModelName,
                VectorIdentity = KnowledgeEmbeddingVectorPolicy.LegacyIdentity,
                Dimension = 0
            };
        }

        var provider = ResolveProvider();

        // Resolve the configured embedding name ONCE to a model actually installed here, and return it so the ingestion lane
        // stamps the exact producing model; the search lane resolves identically, keeping chunk and query vectors comparable.
        var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken);
        var embeddingModelName = resolution.Name;
        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = embeddingModelName,
            ProviderName = _options.EmbeddingProviderName
        });

        var batchSize = Math.Max(1, _options.MaxEmbeddingBatchSize);
        var blobs = new List<byte[]>(chunkContents.Count);

        // The vector width comes from this run's first embedding, not a static constant, so any model's native dimension is
        // honored; every later vector is checked against it, and a mismatch is a broken or mixed model that fails the document.
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
                generated = await generator.GenerateAsync(batch, options: null, cancellationToken);
            }
            catch (HttpRequestException exception) when (exception.StatusCode is not null)
            {
                // The server ANSWERED with a non-2xx: it is reachable and the model is loaded, so "install a model" is the
                // wrong remediation. Carry the exception so KnowledgeIngestionService can log the server's own diagnostic.
                throw new KnowledgeIngestionException(EmbeddingRejectedReason, exception);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaUnavailableException or InvalidOperationException)
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

        return new KnowledgeEmbeddingResult
        {
            Vectors = blobs,
            ResolvedModel = embeddingModelName,
            VectorIdentity = vectorIdentity!,
            Dimension = dimension
        };
    }

    public async Task<int?> ResolveEmbeddingContextWindowAsync(CancellationToken cancellationToken)
    {
        try
        {
            var provider = _providerResolver.ResolveProvider(_options.EmbeddingProviderName);
            var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken);

            // A non-confident resolution is a bare fallback name, not an actually-installed model — its window is unknown.
            if (!resolution.IsConfident)
            {
                return null;
            }

            var installed = await provider.ListModelsAsync(cancellationToken);
            var descriptor = installed.FirstOrDefault(model =>
                string.Equals(model.ModelName, resolution.Name, StringComparison.OrdinalIgnoreCase));

            return descriptor?.MaxContextTokens is int window && window > 0 ? window : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaUnavailableException or InvalidOperationException)
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
            var resolution = await _embeddingModelResolver.ResolveAsync(provider, cancellationToken);
            var identity = KnowledgeEmbeddingVectorPolicy.TryCreateExpectedIdentity(resolution, _options.EmbeddingVectorMode);
            return identity is null
                ? null
                : new KnowledgeEmbeddingDescriptor
                {
                    ResolvedModel = resolution.Name,
                    VectorIdentity = identity,
                    Dimension = KnowledgeEmbeddingVectorPolicy.MatryoshkaWidth
                };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaUnavailableException or InvalidOperationException)
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
