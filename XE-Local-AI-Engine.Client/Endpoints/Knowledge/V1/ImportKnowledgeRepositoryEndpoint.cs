namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>Starts bounded background indexing for a previously registered local Git repository.</summary>
public sealed class ImportKnowledgeRepositoryEndpoint : Endpoint<ImportKnowledgeRepositoryRequest, ImportKnowledgeRepositoryResponse>
{
    private readonly bool _developmentRegistered;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IServiceScopeFactory _scopeFactory;

    public ImportKnowledgeRepositoryEndpoint(IServiceScopeFactory scopeFactory, IOptions<DevelopmentOptions> options, INodeRuntimeSettings runtimeSettings)
    {
        // The options carry the startup value, which decided whether the repository services exist; the live switch closes the rest.
        _developmentRegistered = (options ?? throw new ArgumentNullException(nameof(options))).Value.Enabled;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.RepositoryImport);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ImportKnowledgeRepositoryRequest req, CancellationToken ct)
    {
        if (!_developmentRegistered || !await _runtimeSettings.GetDevelopmentEnabledAsync(ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var importer = scope.ServiceProvider.GetRequiredService<IKnowledgeRepositoryImportService>();
            var result = await importer.ImportAsync(req.SelectedFolderId, req.CollectionId, ct);
            if (result.QueueCapacityReached)
            {
                HttpContext.Response.Headers.RetryAfter = "5";
            }

            await Send.OkAsync(new ImportKnowledgeRepositoryResponse
                {
                    CollectionId = result.CollectionId,
                    DiscoveredFiles = result.DiscoveredFiles,
                    AddedDocuments = result.AddedDocuments,
                    UpdatedDocuments = result.UpdatedDocuments,
                    RemovedDocuments = result.RemovedDocuments,
                    DeduplicatedDocuments = result.DeduplicatedDocuments,
                    EnqueuedDocuments = result.EnqueuedDocuments,
                    SkippedFiles = result.SkippedFiles,
                    QueueCapacityReached = result.QueueCapacityReached
                },
                ct);
        }
        // Only the rejections the CALLER can act on are echoed as 400; a bare InvalidOperationException must stay OUT, or an environment failure inside the importer (an
        // unreadable Git index, an unopenable file) becomes a client error: those are KnowledgeRepositoryReadException (global 500) and KnowledgeRepositoryImportRejectedException (global 400).
        catch (Exception exception) when (exception is ArgumentException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
