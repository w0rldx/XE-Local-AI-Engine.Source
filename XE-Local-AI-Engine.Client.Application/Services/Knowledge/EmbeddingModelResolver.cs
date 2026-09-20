namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     Resolves the ACTUAL embedding model name to hand to a provider's embedding generator from the configured
///     <see cref="KnowledgeBaseOptions.EmbeddingModelName" /> and the models installed on the resolved provider.
/// </summary>
/// <remarks>
///     The configured default is an Ollama-style name such as <c>nomic-embed-text</c>; on a llama.cpp node the same
///     weights are installed under a <c>&lt;repo&gt;:&lt;quant&gt;</c> GGUF name that never equals it, so a literal
///     pass-through fails even with a matching embedding GGUF present. Bridging that gap makes knowledge-base embedding
///     work on either runtime, while an installed exact configured name is kept as-is. The chunk-vector and
///     query-vector lanes MUST share one resolved name so the two vector sets stay comparable.
/// </remarks>
public interface IEmbeddingModelResolver
{
    /// <summary>Resolves the embedding model name to use on <paramref name="provider" />.</summary>
    /// <remarks>
    ///     Resolution order: (1) the configured name when a case-insensitively equal model is installed, confident;
    ///     (2) otherwise the first installed model whose NAME identifies an embedding model
    ///     (<see cref="ModelKindDetector.IsEmbeddingName" />), by ordinal-ignore-case name order, confident;
    ///     (3) otherwise the configured name unchanged and NOT confident, so the caller's graceful not-available path
    ///     fires. A transport failure while enumerating installed models also degrades to (3), NOT confident, never throwing.
    /// </remarks>
    Task<EmbeddingModelResolution> ResolveAsync(ILocalModelProvider provider, CancellationToken cancellationToken);
}

/// <summary>The outcome of one <see cref="IEmbeddingModelResolver.ResolveAsync" /> call.</summary>
/// <remarks>
///     <see cref="Name" /> is always the model name to embed with; ingestion and search consume only that.
///     <see cref="IsConfident" /> marks a REAL resolution — an installed model was matched, the exact configured name or
///     a fallback GGUF — against a degrade-to-configured-name outcome, where nothing matched or the provider was
///     unreachable. A non-confident name must never be treated as a vector identity: comparing stored vectors or
///     staleness against a mere fallback misclassifies every document as stale during a transient provider outage.
/// </remarks>
public sealed class EmbeddingModelResolution
{
    /// <summary>The model name to hand to the embedding generator.</summary>
    public required string Name { get; init; }

    /// <summary>Whether an installed model was actually matched (as opposed to a bare fallback).</summary>
    public required bool IsConfident { get; init; }

    /// <summary>Content-free fingerprint of the matched immutable installed-inventory record.</summary>
    public string RevisionFingerprint { get; init; } = "unresolved";
}

/// <inheritdoc />
public sealed class EmbeddingModelResolver : IEmbeddingModelResolver
{
    private readonly KnowledgeBaseOptions _options;

    public EmbeddingModelResolver(IOptions<KnowledgeBaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<EmbeddingModelResolution> ResolveAsync(ILocalModelProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var configuredName = _options.EmbeddingModelName;

        IReadOnlyList<LocalModelDescriptor> installed;
        try
        {
            installed = await provider.ListModelsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaUnavailableException or InvalidOperationException)
        {
            // Provider process down, transport error or unmapped provider: keep the configured name so the caller's
            // graceful "not available" path fires. NOT confident, never a vector identity; no model or chunk text involved.
            return new EmbeddingModelResolution { Name = configuredName, IsConfident = false };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A provider request TIMEOUT surfaces as TaskCanceledException even though the CALLER never cancelled; the
            // filter separates them, so an unfired caller token means timeout and degrades, and a real cancel rethrows.
            return new EmbeddingModelResolution { Name = configuredName, IsConfident = false };
        }

        // (1) Exact configured name is installed → keep it (an Ollama node with nomic-embed-text is unaffected).
        var exactMatch = installed.FirstOrDefault(descriptor =>
            string.Equals(descriptor.ModelName, configuredName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
        {
            return CreateConfidentResolution(configuredName, exactMatch);
        }

        // (2) First installed embedding-named model in a deterministic order (for example a nomic-embed GGUF).
        var embeddingModel = installed
                             .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.ModelName)
                                                  && ModelKindDetector.IsEmbeddingName(descriptor.ModelName))
                             .OrderBy(descriptor => descriptor.ModelName, StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault();
        if (embeddingModel is not null)
        {
            return CreateConfidentResolution(embeddingModel.ModelName, embeddingModel);
        }

        // (3) Nothing installed matches → keep the configured name, NOT confident (graceful failure downstream).
        return new EmbeddingModelResolution { Name = configuredName, IsConfident = false };
    }

    private static EmbeddingModelResolution CreateConfidentResolution(string resolvedName, LocalModelDescriptor descriptor)
    {
        // Prefer the provider's immutable content/source revision. Older providers may not expose one, so retain a
        // deterministic inventory-record fallback; hashing prevents the revision or timestamp becoming identifier text.
        var modifiedTicks = descriptor.ModifiedAt?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var sizeBytes = descriptor.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var immutableRevision = string.IsNullOrWhiteSpace(descriptor.RevisionFingerprint)
            ? $"inventory-record:{sizeBytes}:{modifiedTicks}"
            : $"provider-revision:{descriptor.RevisionFingerprint}";
        var material = string.Create(CultureInfo.InvariantCulture,
            $"inventory-v1\n{descriptor.ProviderName}\n{resolvedName}\n{immutableRevision}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new EmbeddingModelResolution
        {
            Name = resolvedName,
            IsConfident = true,
            RevisionFingerprint = $"inventory-v1:{Convert.ToHexStringLower(digest)}"
        };
    }
}
