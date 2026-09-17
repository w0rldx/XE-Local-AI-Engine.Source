namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Node-settings tools of <see cref="NodeAdminMcpTools" />: reading the restricted agentic view and applying a
///     partial update to the same whitelist.
/// </summary>
public sealed partial class NodeAdminMcpTools
{
    [McpServerTool(Name = "get_node_settings")]
    [Description("Get only the restricted core node settings available to agentic automation.")]
    public Task<NodeSettingsAgenticView> GetNodeSettingsAsync(CancellationToken cancellationToken) =>
        InvokeAuditedAsync("get_node_settings", [], () =>
            _nodeSettingsAdministrationService.GetAgenticViewAsync(cancellationToken));

    [McpServerTool(Name = "update_node_settings")]
    [Description("Apply a partial update to the exact restricted 18-field agentic node-settings whitelist.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpNodeSettingsUpdateResponse> UpdateNodeSettingsAsync(CancellationToken cancellationToken,
        string? default_model_name = null,
        bool? enable_tools = null,
        IReadOnlyList<string>? tool_capable_models = null,
        string? hugging_face_default_quant = null,
        int? llama_max_loaded_processes = null,
        int? llama_idle_time_to_live_seconds = null,
        bool? keep_model_warm_enabled = null,
        string? keep_model_warm_model_name = null,
        int? keep_model_warm_interval_seconds = null,
        int? max_message_request_timeout_seconds = null,
        int? chat_cache_reuse = null,
        string? speculative_mode = null,
        string? speculative_draft_model_name = null,
        int? speculative_draft_max_tokens = null,
        int? speculative_draft_gpu_layers = null,
        string? kv_cache_type = null,
        string? reranker_model_name = null,
        string? auto_effort_fast_model_name = null)
#pragma warning restore IDE1006
    {
        var arguments = AuditArguments(("default_model_name", default_model_name),
            ("enable_tools", enable_tools),
            ("tool_capable_models", tool_capable_models),
            ("hugging_face_default_quant", hugging_face_default_quant),
            ("llama_max_loaded_processes", llama_max_loaded_processes),
            ("llama_idle_time_to_live_seconds", llama_idle_time_to_live_seconds),
            ("keep_model_warm_enabled", keep_model_warm_enabled),
            ("keep_model_warm_model_name", keep_model_warm_model_name),
            ("keep_model_warm_interval_seconds", keep_model_warm_interval_seconds),
            ("max_message_request_timeout_seconds", max_message_request_timeout_seconds),
            ("chat_cache_reuse", chat_cache_reuse),
            ("speculative_mode", speculative_mode),
            ("speculative_draft_model_name", speculative_draft_model_name),
            ("speculative_draft_max_tokens", speculative_draft_max_tokens),
            ("speculative_draft_gpu_layers", speculative_draft_gpu_layers),
            ("kv_cache_type", kv_cache_type),
            ("reranker_model_name", reranker_model_name),
            ("auto_effort_fast_model_name", auto_effort_fast_model_name));
        return await InvokeAuditedAsync("update_node_settings", arguments, async () =>
        {
            var result = await _nodeSettingsAdministrationService.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
            {
                DefaultModelName = default_model_name,
                EnableTools = enable_tools,
                ToolCapableModels = tool_capable_models,
                HuggingFaceDefaultQuant = hugging_face_default_quant,
                LlamaMaxLoadedProcesses = llama_max_loaded_processes,
                LlamaIdleTimeToLiveSeconds = llama_idle_time_to_live_seconds,
                KeepModelWarmEnabled = keep_model_warm_enabled,
                KeepModelWarmModelName = keep_model_warm_model_name,
                KeepModelWarmIntervalSeconds = keep_model_warm_interval_seconds,
                MaxMessageRequestTimeoutSeconds = max_message_request_timeout_seconds,
                ChatCacheReuse = chat_cache_reuse,
                SpeculativeMode = speculative_mode,
                SpeculativeDraftModelName = speculative_draft_model_name,
                SpeculativeDraftMaxTokens = speculative_draft_max_tokens,
                SpeculativeDraftGpuLayers = speculative_draft_gpu_layers,
                KvCacheType = kv_cache_type,
                RerankerModelName = reranker_model_name,
                AutoEffortFastModelName = auto_effort_fast_model_name
            }, cancellationToken);
            return result.ToResponse();
        }, static response => !response.Updated);
    }
}
