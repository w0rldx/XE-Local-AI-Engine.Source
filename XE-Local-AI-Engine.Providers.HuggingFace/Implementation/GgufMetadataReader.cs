namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Public <see cref="IGgufMetadataReader" /> over the provider-internal <see cref="GgufHeaderReader" />. Keeps the
///     header reader internal to this provider while still exposing the optimizer's MoE/param/quant/context inputs across
///     the layer boundary. Stateless wrapper — registered as a singleton.
/// </summary>
public sealed class GgufMetadataReader : IGgufMetadataReader
{
    private readonly GgufHeaderReader _headerReader;
    private readonly ILogger<GgufMetadataReader> _logger;

    internal GgufMetadataReader(GgufHeaderReader headerReader, ILogger<GgufMetadataReader> logger)
    {
        ArgumentNullException.ThrowIfNull(headerReader);
        ArgumentNullException.ThrowIfNull(logger);

        _headerReader = headerReader;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GgufModelMetadata> ReadMetadataAsync(string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var header = await _headerReader.ReadHeaderFromFileAsync(filePath, ct).ConfigureAwait(false);

        var expertCount = header.ExpertCount switch
        {
            null => (int?)null,
            <= 0 => null,
            > int.MaxValue => int.MaxValue,
            var value => (int)value
        };

        _logger.LogDebug("Read GGUF metadata for an installed model: params={ParamCount}, quant={Quant}, isMoe={IsMoe}.",
            header.ParamCount,
            header.QuantType,
            header.IsMoe);

        return new GgufModelMetadata(header.ParamCount, header.QuantType, header.ContextLength, expertCount, header.IsMoe);
    }
}
