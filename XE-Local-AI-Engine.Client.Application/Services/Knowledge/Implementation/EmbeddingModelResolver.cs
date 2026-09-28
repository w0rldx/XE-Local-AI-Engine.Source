namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

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
            return new EmbeddingModelResolution
            {
                Name = configuredName,
                IsConfident = false
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A provider request TIMEOUT surfaces as TaskCanceledException even though the CALLER never cancelled; the
            // filter separates them, so an unfired caller token means timeout and degrades, and a real cancel rethrows.
            return new EmbeddingModelResolution
            {
                Name = configuredName,
                IsConfident = false
            };
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
        return new EmbeddingModelResolution
        {
            Name = configuredName,
            IsConfident = false
        };
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
