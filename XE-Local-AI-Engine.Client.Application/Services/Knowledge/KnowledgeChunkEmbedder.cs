namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Runtime.InteropServices;
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
    private const string EmbeddingUnavailableReason =
        "The embedding model is not available. Pull the configured embedding model (for example nomic-embed-text) and retry.";

    private const string EmbeddingIncompleteReason =
        "The embedding model returned an incomplete result. Ensure the configured embedding model is loaded and retry.";

    private const string DimensionMismatchReason =
        "The embedding model produced vectors of an unexpected dimension. Reindex with a matching embedding model.";

    private readonly KnowledgeBaseOptions _options;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IKnowledgeEmbeddingPrefixer _prefixer;

    public KnowledgeChunkEmbedder(ILocalModelProviderResolver providerResolver,
        IKnowledgeEmbeddingPrefixer prefixer,
        IOptions<KnowledgeBaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(providerResolver);
        ArgumentNullException.ThrowIfNull(prefixer);
        ArgumentNullException.ThrowIfNull(options);

        _providerResolver = providerResolver;
        _prefixer = prefixer;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<byte[]>> EmbedAsync(IReadOnlyList<string> chunkContents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunkContents);
        if (chunkContents.Count == 0)
        {
            return [];
        }

        var provider = ResolveProvider();
        using var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = _options.EmbeddingModelName,
            ProviderName = _options.EmbeddingProviderName
        });

        var batchSize = Math.Max(1, _options.MaxEmbeddingBatchSize);
        var blobs = new List<byte[]>(chunkContents.Count);

        for (var offset = 0; offset < chunkContents.Count; offset += batchSize)
        {
            var count = Math.Min(batchSize, chunkContents.Count - offset);
            var batch = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                // Prepend the document-intent prefix only on the text handed to the generator — never into stored content.
                batch.Add(_prefixer.ForDocument(chunkContents[offset + index]));
            }

            IReadOnlyList<Microsoft.Extensions.AI.Embedding<float>> generated;
            try
            {
                generated = await generator.GenerateAsync(batch, options: null, cancellationToken).ConfigureAwait(false);
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

            blobs.AddRange(generated.Select(embedding => ToEmbeddingBlob(embedding.Vector)));
        }

        return blobs;
    }

    private byte[] ToEmbeddingBlob(ReadOnlyMemory<float> vector)
    {
        if (vector.Length != _options.EmbeddingDimension)
        {
            throw new KnowledgeIngestionException(DimensionMismatchReason);
        }

        return MemoryMarshal.AsBytes(vector.Span).ToArray();
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
