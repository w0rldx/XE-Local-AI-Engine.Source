namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;

public sealed class ListLocalModelsEndpoint(ILocalModelCatalogService catalogService) : EndpointWithoutRequest<ListLocalModelsResponse>
{
    private readonly ILocalModelCatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.Models);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var catalog = await _catalogService.GetCatalogAsync(ct).ConfigureAwait(false);
        var cloudModels = ToCloudModelResponses(catalog);
        var externalModels = LocalModelsMapper.ToExternalProviderModelResponses(catalog.ExternalModels, catalog.SelectedModelName);

        // A null model list is the catalog's only unavailability signal: Ollama could not be reached, so the response
        // degrades to the installed-GGUF + cloud + external entries the other sources still supplied.
        var response = catalog.OllamaModels is null
            ? LocalModelsMapper.ToUnavailableListResponse(catalog.SelectedModelName,
                catalog.ConfiguredDefaultModelName,
                "Local model provider is unavailable.",
                cloudModels,
                catalog.InstalledGgufModels,
                externalModels)
            : LocalModelsMapper.ToListResponse(catalog.OllamaModels,
                catalog.SelectedModelName,
                catalog.ConfiguredDefaultModelName,
                catalog.Classifications,
                cloudModels,
                catalog.InstalledGgufModels,
                externalModels);

        await Send.OkAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Codex first, then Azure Foundry. The picker groups by Provider, so the two cloud families stay visually
    ///     separated; both trail the node-local entries the list mapper puts first.
    /// </summary>
    private static IReadOnlyList<LocalModelResponse> ToCloudModelResponses(LocalModelCatalog catalog)
    {
        var codexModels = catalog.HasUsableCodexSession
            ? LocalModelsMapper.ToCodexCloudModelResponses(catalog.SelectedModelName)
            : [];

        var azureModels = catalog.AzureFoundryConnection is { Models.Count: > 0 } connection
            ? LocalModelsMapper.ToAzureFoundryCloudModelResponses(connection, catalog.SelectedModelName)
            : [];

        return codexModels.Concat(azureModels).ToArray();
    }
}
