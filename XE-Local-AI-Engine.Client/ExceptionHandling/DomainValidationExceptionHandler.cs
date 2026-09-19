namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Automation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;
using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Export;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Maps the single-message domain validation exceptions to a 400 whose body is byte-identical to the
///     <c>AddError(exception.Message) + Send.ErrorsAsync()</c> pair the endpoints used to write by hand — see
///     <see cref="FastEndpointsProblemWriter" />, which owns that reproduction. New single-message validation
///     exceptions belong in the switch below rather than in a per-endpoint catch. Multi-error types
///     (<c>GraphWorkflowValidationException</c>) and aggregate ones (<c>SelectedFolderValidationException</c>) are
///     deliberately out of scope — they do not map to one failure at one status.
///     <para>
///         A type whose status is not the same at every endpoint that raises it also stays out:
///         <c>DevelopmentWorkspaceSecurityException</c> is answered 409 by the patch/next-action endpoints and 400 by
///         the register/create ones, so a global 400 would silently move three routes.
///     </para>
/// </summary>
public sealed class DomainValidationExceptionHandler : IExceptionHandler
{
    private readonly ILogger<DomainValidationExceptionHandler> _logger;

    public DomainValidationExceptionHandler(ILogger<DomainValidationExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is not (ScheduledJobValidationException
            or CustomToolValidationException
            or McpServerValidationException
            or SlashCommandValidationException
            or PlaybookActionValidationException
            or AgentDefinitionValidationException
            or AgentSkillValidationException
            or WorkSessionValidationException
            or DevWorkflowValidationException
            or EntraConnectionNotConfiguredException
            or SkillImportException
            or AppUpdateException
            or ExternalProviderValidationException
            or NodeSettingsUnreadableException
            or NodeChatInvalidBranchSelectionException
            or EvaluationRejectedException
            or TrainingExportRejectedException
            or TrainingRunRejectedException
            or KnowledgeRepositoryImportRejectedException
            or DevelopmentTemplateAliasInUseException
            or DevelopmentTemplateMaterializationException
            or ExternalAppValidationException
            or ExternalAppConfigurationException
            or ExternalAppManifestException))
        {
            return false;
        }

        _logger.LogWarning(exception,
            "Handled domain validation exception while processing {Method} {Path}. StatusCode: {StatusCode}. TraceId: {TraceId}. UserId: {UserId}. ExceptionType: {ExceptionType}",
            RequestLogSanitizer.Sanitize(httpContext.Request.Method),
            RequestLogSanitizer.Sanitize(httpContext.Request.Path.Value),
            StatusCodes.Status400BadRequest,
            httpContext.TraceIdentifier,
            httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "anonymous",
            exception.GetType().Name);

        await FastEndpointsProblemWriter.WriteAsync(httpContext, exception.Message, StatusCodes.Status400BadRequest, cancellationToken);

        return true;
    }
}
