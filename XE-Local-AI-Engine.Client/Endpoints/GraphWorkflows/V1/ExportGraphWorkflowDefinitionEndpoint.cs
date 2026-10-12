namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>One definition as a share file, sent as a download; the graph is the same wire graph the detail route returns.</summary>
public sealed class ExportGraphWorkflowDefinitionEndpoint : Endpoint<GraphWorkflowDefinitionRequest, GraphWorkflowExportResponse>
{
    private readonly IGraphWorkflowDefinitionService _definitions;

    public ExportGraphWorkflowDefinitionEndpoint(IGraphWorkflowDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.DefinitionExport);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var definition = await _definitions.GetAsync(req.DefinitionId, ct);
        HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{GraphWorkflowShareFile.FileName(definition.Name)}\"";
        await Send.OkAsync(definition.ToExportResponse(), ct);
    }
}
