namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;

/// <summary>Translates an application-layer <see cref="ModelCatalogSnapshot" /> into the sanitized catalog-info DTO.</summary>
internal static class ModelCatalogMapper
{
    /// <summary>The tested entries in response order: <c>totalParamsB</c> ascending, then id.</summary>
    public static IReadOnlyList<ModelCatalogEntry> TestedEntries(this ModelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Document.Models
                       .Where(entry => entry.Tested)
                       .OrderBy(entry => entry.TotalParamsB)
                       .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                       .ToList();
    }

    /// <summary>Maps the snapshot; <paramref name="testedVerdicts" /> holds one verdict per <see cref="TestedEntries" /> item, a missing one reads as <c>Unknown</c>.</summary>
    public static ModelCatalogInfoResponse ToResponse(this ModelCatalogSnapshot snapshot,
        bool refreshSourceConfigured,
        IReadOnlyList<GgufFitVerdict> testedVerdicts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(testedVerdicts);

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
            RefreshSourceConfigured = refreshSourceConfigured,
            TestedModels = snapshot.TestedEntries()
                                   .Select((entry, index) => new ModelCatalogTestedModelResponse
                                   {
                                       Id = entry.Id,
                                       DisplayName = entry.DisplayName,
                                       Publisher = entry.Publisher,
                                       GgufRepo = entry.GgufRepo,
                                       License = entry.License,
                                       TotalParamsB = entry.TotalParamsB,
                                       Notes = entry.Notes,
                                       TestedQuant = entry.TestedQuant ?? string.Empty,
                                       TestedSizeBytes = entry.TestedSizeBytes.GetValueOrDefault(),
                                       FitVerdict = (index < testedVerdicts.Count ? testedVerdicts[index] : GgufFitVerdict.Unknown).ToString()
                                   })
                                   .ToList()
        };
    }
}
