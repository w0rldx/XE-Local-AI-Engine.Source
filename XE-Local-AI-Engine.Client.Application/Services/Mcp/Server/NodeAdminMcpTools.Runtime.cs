namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using ModelContextProtocol.Server;

/// <summary>
///     Runtime tools of <see cref="NodeAdminMcpTools" />: node status, the installed and recommended llama.cpp
///     versions, and the managed runtime acquisition an agent can start and poll.
/// </summary>
public sealed partial class NodeAdminMcpTools
{
    [McpServerTool(Name = "get_status")]
    [Description("Get the node version, uptime, selected default model, and number of loaded llama.cpp processes.")]
    public Task<McpNodeStatusResponse> GetStatusAsync(CancellationToken cancellationToken) =>
        InvokeAuditedAsync("get_status", [], async () =>
        {
            var settings = await _nodeSettingsAdministrationService.GetAgenticViewAsync(cancellationToken).ConfigureAwait(false);
            var runtime = await _runtimeAdministrationService.GetStatusAsync(refresh: false, cancellationToken).ConfigureAwait(false);
            return new McpNodeStatusResponse(GetVersion(), GetProcessUptimeSeconds(), settings.DefaultModelName, runtime.RunningProcessCount);
        });

    [McpServerTool(Name = "get_runtime_status")]
    [Description("Get the installed and recommended llama.cpp runtime versions without refreshing the remote catalog.")]
    public Task<McpRuntimeStatusResponse> GetRuntimeStatusAsync(CancellationToken cancellationToken) =>
        InvokeAuditedAsync("get_runtime_status", [], async () =>
        {
            var status = await _runtimeAdministrationService.GetStatusAsync(refresh: false, cancellationToken).ConfigureAwait(false);
            return new McpRuntimeStatusResponse(status.Installed?.Tag,
                status.RecommendedTag,
                status.UpstreamLatestTag,
                status.UpdateAvailable,
                status.IsOffline,
                status.RunningProcessCount);
        });

    [McpServerTool(Name = "start_runtime_acquisition")]
    [Description("Start acquiring the managed llama.cpp runtime. Omit variant to select the best local backend automatically.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpRuntimeAcquisitionStartResponse> StartRuntimeAcquisitionAsync(CancellationToken cancellationToken,
        [Description("Optional backend: cpu, cuda, or vulkan.")]
        string? variant = null)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("start_runtime_acquisition", AuditArguments(("variant", variant)), async () =>
        {
            if (!TryParseVariant(variant, out var parsedVariant))
            {
                return new McpRuntimeAcquisitionStartResponse("rejected", null, McpAdminToolFailureCodes.InvalidVariant,
                    "Variant must be cpu, cuda, vulkan, or omitted.");
            }

            var result = await _runtimeAdministrationService.StartAcquisitionAsync(parsedVariant, cancellationToken).ConfigureAwait(false);
            return result.Accepted
                ? new McpRuntimeAcquisitionStartResponse("accepted", result.Variant)
                : new McpRuntimeAcquisitionStartResponse("busy", result.Variant, McpAdminToolFailureCodes.Busy, result.DisplayMessage);
        }, static response => response.FailureCode is not null).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_runtime_acquisition")]
    [Description("Get the current or most recent sanitized llama.cpp runtime acquisition progress.")]
    public Task<McpRuntimeAcquisitionResponse> GetRuntimeAcquisition() =>
        InvokeAuditedAsync("get_runtime_acquisition", [], () =>
            Task.FromResult(_runtimeAdministrationService.GetAcquisitionStatus().ToResponse()));
}
