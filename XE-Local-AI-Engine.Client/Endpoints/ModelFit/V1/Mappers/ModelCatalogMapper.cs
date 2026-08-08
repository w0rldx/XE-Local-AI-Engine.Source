namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>Translates an application-layer <see cref="ModelCatalogSnapshot" /> into the sanitized catalog-info DTO.</summary>
internal static class ModelCatalogMapper
{
    public static ModelCatalogInfoResponse ToResponse(this ModelCatalogSnapshot snapshot, bool refreshSourceConfigured)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ModelCatalogInfoResponse
        {
            CatalogVersion = snapshot.Document.CatalogVersion,
            UpdatedAt = snapshot.Document.UpdatedAt,
            Source = snapshot.Source switch
            {
                ModelCatalogSource.Remote => "remote",
                ModelCatalogSource.RemoteLastGood => "remoteLastGood",
                _ => "bundled"
            },
            FetchedAtUtc = snapshot.FetchedAtUtc?.ToUnixTimeMilliseconds(),
            SourceUrl = snapshot.SourceUrl,
            ModelCount = snapshot.Document.Models.Count,
            RefreshSourceConfigured = refreshSourceConfigured
        };
    }
}
