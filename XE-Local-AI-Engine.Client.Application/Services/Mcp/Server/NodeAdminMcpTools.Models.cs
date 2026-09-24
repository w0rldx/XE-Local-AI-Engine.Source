namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Model tools of <see cref="NodeAdminMcpTools" />: starting, polling and cancelling a background GGUF pull,
///     deleting an installed model, and selecting the node default.
/// </summary>
public sealed partial class NodeAdminMcpTools
{
    [McpServerTool(Name = "start_model_pull")]
    [Description("Start or rejoin a background GGUF model pull from Hugging Face.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpModelPullStartResponse> StartModelPullAsync([Description("Hugging Face repository id.")] string repo_id,
        CancellationToken cancellationToken,
        [Description("Optional exact GGUF file name.")]
        string? file_name = null,
        [Description("Optional quant label when file_name is omitted.")]
        string? quant = null,
        [Description("Optional branch, tag, or commit revision.")]
        string? revision = null,
        [Description("Whether to also pull the repo's mmproj projector when it ships one. Defaults to true; false installs the weights only.")]
        bool include_projector = true)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("start_model_pull",
            AuditArguments(("repo_id", repo_id),
                ("file_name", file_name),
                ("quant", quant),
                ("revision", revision),
                ("include_projector", include_projector)),
            async () =>
            {
                if (string.IsNullOrWhiteSpace(repo_id))
                {
                    return new McpModelPullStartResponse
                    {
                        Status = "rejected",
                        ModelName = null,
                        OperationId = null,
                        FailureCode = McpAdminToolFailureCodes.InvalidRequest,
                        DisplayMessage = "A repository id is required."
                    };
                }

                try
                {
                    var ticket = await _ggufDownloadCoordinator.StartAsync(new GgufModelRequest
                    {
                        RepoId = repo_id.Trim(),
                        FileName = NullIfWhiteSpace(file_name),
                        Quant = NullIfWhiteSpace(quant),
                        Revision = NullIfWhiteSpace(revision),
                        IncludeProjector = include_projector
                    }, cancellationToken);
                    return new McpModelPullStartResponse
                    {
                        Status = ticket.AlreadyInFlight ? "already_in_flight" : "accepted",
                        ModelName = ticket.ModelName,
                        OperationId = ticket.OperationId == Guid.Empty ? null : ticket.OperationId.ToString("D")
                    };
                }
                catch (GgufAcquisitionConflictException exception)
                {
                    return new McpModelPullStartResponse
                    {
                        Status = "rejected",
                        ModelName = null,
                        OperationId = null,
                        FailureCode = McpAdminToolFailureCodes.ModelPullConflict,
                        DisplayMessage = exception.Message
                    };
                }
                catch (HuggingFaceDownloadException exception)
                {
                    return new McpModelPullStartResponse
                    {
                        Status = "rejected",
                        ModelName = null,
                        OperationId = null,
                        FailureCode = McpAdminWireNames.DownloadErrorCode(exception.Reason),
                        DisplayMessage = exception.Message
                    };
                }
            },
            static response => response.FailureCode is not null);
    }

    [McpServerTool(Name = "get_model_pull")]
    [Description("Poll a background GGUF model pull by canonical model name.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public Task<McpModelPullResponse> GetModelPull([Description("Canonical model name returned by start_model_pull.")] string model_name)
#pragma warning restore IDE1006
    {
        return InvokeAuditedAsync("get_model_pull", AuditArguments(("model_name", model_name)), () =>
        {
            if (string.IsNullOrWhiteSpace(model_name))
            {
                return Task.FromResult(new McpModelPullResponse
                {
                    Status = "not_found",
                    ModelName = null,
                    Phase = null,
                    CompletedBytes = null,
                    TotalBytes = null,
                    SanitizedError = null,
                    OperationId = null,
                    FailureCode = McpAdminToolFailureCodes.InvalidRequest,
                    DisplayMessage = "A model name is required."
                });
            }

            var status = _ggufDownloadCoordinator.GetStatus(model_name.Trim());
            if (status is null)
            {
                return Task.FromResult(new McpModelPullResponse
                {
                    Status = "not_found",
                    ModelName = null,
                    Phase = null,
                    CompletedBytes = null,
                    TotalBytes = null,
                    SanitizedError = null,
                    OperationId = null,
                    FailureCode = McpAdminToolFailureCodes.ModelPullNotFound,
                    DisplayMessage = "Model pull not found."
                });
            }

            var operationId = status.OperationId == Guid.Empty ? null : status.OperationId.ToString("D");
            return Task.FromResult(new McpModelPullResponse
            {
                Status = "ok",
                ModelName = status.ModelName,
                Phase = ToWirePhase(status.Phase),
                CompletedBytes = status.CompletedBytes,
                TotalBytes = status.TotalBytes,
                SanitizedError = status.SanitizedError,
                OperationId = operationId,
                FailureCode = status.Phase == GgufDownloadPhase.Failed ? McpAdminWireNames.DownloadErrorCode(status.ErrorCode) : null,
                DisplayMessage = status.SanitizedError
            });
        }, static response => response.FailureCode is not null);
    }

    [McpServerTool(Name = "cancel_model_pull")]
    [Description("Request cooperative cancellation of a background GGUF model pull.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public Task<McpModelPullCancelResponse> CancelModelPull([Description("Canonical model name returned by start_model_pull.")] string model_name) =>
        InvokeAuditedAsync("cancel_model_pull", AuditArguments(("model_name", model_name)), () =>
                Task.FromResult(new McpModelPullCancelResponse
                {
                    Cancelled = !string.IsNullOrWhiteSpace(model_name)
                                && _ggufDownloadCoordinator.Cancel(model_name.Trim())
                }),
            static response => !response.Cancelled);
#pragma warning restore IDE1006

    [McpServerTool(Name = "delete_model")]
    [Description("Delete a locally installed model through the node's coordinated deletion service.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpModelDeleteResponse> DeleteModelAsync(string model_name, CancellationToken cancellationToken)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("delete_model", AuditArguments(("model_name", model_name)), async () =>
        {
            var result = await _localModelAdministrationService.DeleteAsync(model_name, cancellationToken);
            return new McpModelDeleteResponse
            {
                Deleted = result.Deleted,
                ModelName = result.ModelName,
                FailureCode = result.FailureCode,
                DisplayMessage = result.DisplayMessage
            };
        }, static response => !response.Deleted);
    }

    [McpServerTool(Name = "set_default_model")]
    [Description("Select an installed local model as the node default.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpDefaultModelResponse> SetDefaultModelAsync(string model_name, CancellationToken cancellationToken)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("set_default_model", AuditArguments(("model_name", model_name)), async () =>
        {
            var result = await _localModelAdministrationService.SelectDefaultAsync(model_name,
                LocalModelSelectionPolicy.InstalledLocalOnly,
                cancellationToken);
            return new McpDefaultModelResponse
            {
                Updated = result.Succeeded,
                SelectedModelName = result.SelectedModelName,
                PreviousDefault = result.PreviousModelName,
                FailureCode = result.FailureCode,
                DisplayMessage = result.DisplayMessage
            };
        }, static response => !response.Updated);
    }
}
