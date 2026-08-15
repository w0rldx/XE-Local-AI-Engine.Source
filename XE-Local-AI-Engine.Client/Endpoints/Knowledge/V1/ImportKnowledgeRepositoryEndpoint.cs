namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>Starts bounded background indexing for a previously registered local Git repository.</summary>
public sealed class ImportKnowledgeRepositoryEndpoint(IServiceScopeFactory scopeFactory, IOptions<DevelopmentOptions> options)
    : Endpoint<ImportKnowledgeRepositoryRequest, ImportKnowledgeRepositoryResponse>
{
    private readonly bool _developmentModeEnabled = (options ?? throw new ArgumentNullException(nameof(options))).Value.Enabled;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

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
        if (!_developmentModeEnabled)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var importer = scope.ServiceProvider.GetRequiredService<IKnowledgeRepositoryImportService>();
            var result = await importer.ImportAsync(req.SelectedFolderId, req.CollectionId, ct).ConfigureAwait(false);
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
                ct).ConfigureAwait(false);
        }
        catch (SelectedFolderNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (SelectedFolderConflictException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or DevelopmentWorkspaceSecurityException
                                              or SelectedFolderValidationException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
