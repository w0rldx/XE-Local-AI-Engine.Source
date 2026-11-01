namespace XE_Local_AI_Engine.Client.Services.Models.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Reads the header metadata and tensor list of an installed GGUF, resolving its file from the model registry only.</summary>
public sealed class GgufInspectionService
{
    private readonly IGgufModelStore _modelStore;
    private readonly IGgufMetadataReader _metadataReader;

    public GgufInspectionService(IGgufModelStore modelStore, IGgufMetadataReader metadataReader)
    {
        ArgumentNullException.ThrowIfNull(modelStore);
        ArgumentNullException.ThrowIfNull(metadataReader);
        _modelStore = modelStore;
        _metadataReader = metadataReader;
    }

    /// <summary>Inspects the GGUF registered as <paramref name="modelName" />.</summary>
    /// <exception cref="InstalledGgufModelNotFoundException">No installed GGUF is registered under that name.</exception>
    public async Task<GgufTensorInspection> InspectAsync(string modelName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var path = await _modelStore.ResolveModelFilePathAsync(modelName, cancellationToken)
                   ?? throw new InstalledGgufModelNotFoundException(modelName);
        return await _metadataReader.ReadTensorsAsync(path, cancellationToken);
    }
}
