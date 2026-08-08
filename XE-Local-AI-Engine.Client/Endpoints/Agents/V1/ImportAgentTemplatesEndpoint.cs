namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class ImportAgentTemplatesEndpoint(IAgentTemplateImportService importService)
    : Endpoint<ImportAgentTemplatesRequest, ImportAgentTemplatesResponse>
{
    private readonly IAgentTemplateImportService _importService = importService ?? throw new ArgumentNullException(nameof(importService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.TemplateImport);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ImportAgentTemplatesRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // A bulk additive operation with no single created resource, so it returns 200 with the per-slug outcome rather
        // than 201 + Location.
        var result = await _importService.ImportAsync(req.Slugs ?? [], ct).ConfigureAwait(false);

        await Send.OkAsync(new ImportAgentTemplatesResponse
            {
                Imported = result.Imported,
                SkippedExisting = result.SkippedExisting,
                Unknown = result.Unknown
            },
            ct).ConfigureAwait(false);
    }
}
