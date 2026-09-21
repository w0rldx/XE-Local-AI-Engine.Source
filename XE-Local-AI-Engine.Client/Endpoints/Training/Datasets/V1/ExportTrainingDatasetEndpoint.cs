namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ExportTrainingDatasetEndpoint : Endpoint<ExportTrainingDatasetRequest, ExportTrainingDatasetResponse>
{
    private readonly IDatasetExportService _export;

    public ExportTrainingDatasetEndpoint(IDatasetExportService export)
    {
        ArgumentNullException.ThrowIfNull(export);
        _export = export;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DatasetExport);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ExportTrainingDatasetRequest req, CancellationToken ct)
    {
        var content = await _export.ExportAsync(req.DatasetId, req.Format, ct);
        await Send.OkAsync(new ExportTrainingDatasetResponse
        {
            DatasetId = req.DatasetId,
            Format = req.Format,
            Content = content,
            LineCount = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
        }, ct);
    }
}
