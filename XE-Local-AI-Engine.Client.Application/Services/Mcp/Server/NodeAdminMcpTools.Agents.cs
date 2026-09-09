namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using ModelContextProtocol.Server;

/// <summary>
///     Agent tools of <see cref="NodeAdminMcpTools" />: reading, creating, replacing and deleting a saved agent
///     through the same validation service the operator API uses.
/// </summary>
public sealed partial class NodeAdminMcpTools
{
    [McpServerTool(Name = "get_agent")]
    [Description("Get one saved agent by id or exact name.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentResponse> GetAgentAsync(string agent_id, CancellationToken cancellationToken)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("get_agent", AuditArguments(("agent_id", agent_id)), async () =>
        {
            var record = await _agentDefinitionService.GetByKeyAsync(agent_id, cancellationToken).ConfigureAwait(false);
            return record is null
                ? AgentNotFound()
                : new McpAgentResponse("ok", McpAgentDefinition.FromRecord(record));
        }, static response => response.FailureCode is not null).ConfigureAwait(false);
    }

    [McpServerTool(Name = "create_agent")]
    [Description("Validate and create a saved agent through the same application service used by the operator API.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentResponse> CreateAgentAsync(string name,
        string instructions,
        CancellationToken cancellationToken,
        string? description = null,
        string? model_profile = null,
        string? reasoning_effort = null,
        string kind = "single",
        IReadOnlyList<string>? allowed_tool_names = null,
        IReadOnlyDictionary<string, bool>? tool_approvals = null,
        string? orchestration_topology_json = null,
        bool playbook_enabled = false,
        IReadOnlyList<string>? allowed_skill_ids = null,
        bool default_temporary_chat = false,
        bool memory_extraction_enabled = true,
        bool disable_base_scaffold = false,
        McpGenerationMetadataInput? generation_metadata = null) =>
        await InvokeAuditedAsync("create_agent",
            AgentAuditArguments(name,
                instructions,
                description,
                model_profile,
                reasoning_effort,
                kind,
                allowed_tool_names,
                tool_approvals,
                orchestration_topology_json,
                allowed_skill_ids,
                playbook_enabled,
                default_temporary_chat,
                memory_extraction_enabled,
                disable_base_scaffold,
                generation_metadata),
            () => SaveAgentAsync(id: null,
                name,
                instructions,
                description,
                model_profile,
                reasoning_effort,
                kind,
                allowed_tool_names,
                tool_approvals,
                orchestration_topology_json,
                playbook_enabled,
                allowed_skill_ids,
                default_temporary_chat,
                memory_extraction_enabled,
                disable_base_scaffold,
                generation_metadata,
                cancellationToken),
            static response => response.FailureCode is not null).ConfigureAwait(false);
#pragma warning restore IDE1006

    [McpServerTool(Name = "update_agent")]
    [Description("Fully replace an existing saved agent by id or exact name through the shared validation service.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentResponse> UpdateAgentAsync(string agent_id,
        string name,
        string instructions,
        CancellationToken cancellationToken,
        string? description = null,
        string? model_profile = null,
        string? reasoning_effort = null,
        string kind = "single",
        IReadOnlyList<string>? allowed_tool_names = null,
        IReadOnlyDictionary<string, bool>? tool_approvals = null,
        string? orchestration_topology_json = null,
        bool playbook_enabled = false,
        IReadOnlyList<string>? allowed_skill_ids = null,
        bool default_temporary_chat = false,
        bool memory_extraction_enabled = true,
        bool disable_base_scaffold = false,
        McpGenerationMetadataInput? generation_metadata = null)
    {
        var arguments = AgentAuditArguments(name,
            instructions,
            description,
            model_profile,
            reasoning_effort,
            kind,
            allowed_tool_names,
            tool_approvals,
            orchestration_topology_json,
            allowed_skill_ids,
            playbook_enabled,
            default_temporary_chat,
            memory_extraction_enabled,
            disable_base_scaffold,
            generation_metadata,
            ("agent_id", agent_id));
        return await InvokeAuditedAsync("update_agent", arguments, async () =>
        {
            var existing = await _agentDefinitionService.GetByKeyAsync(agent_id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return AgentNotFound();
            }

            return await SaveAgentAsync(existing.Id,
                name,
                instructions,
                description,
                model_profile,
                reasoning_effort,
                kind,
                allowed_tool_names,
                tool_approvals,
                orchestration_topology_json,
                playbook_enabled,
                allowed_skill_ids,
                default_temporary_chat,
                memory_extraction_enabled,
                disable_base_scaffold,
                generation_metadata,
                cancellationToken).ConfigureAwait(false);
        }, static response => response.FailureCode is not null).ConfigureAwait(false);
    }
#pragma warning restore IDE1006

    [McpServerTool(Name = "delete_agent")]
    [Description("Delete a saved agent by id or exact name.")]
#pragma warning disable IDE1006 // MCP's public JSON contract intentionally uses snake_case.
    public async Task<McpAgentDeleteResponse> DeleteAgentAsync(string agent_id, CancellationToken cancellationToken)
#pragma warning restore IDE1006
    {
        return await InvokeAuditedAsync("delete_agent", AuditArguments(("agent_id", agent_id)), async () =>
        {
            var existing = await _agentDefinitionService.GetByKeyAsync(agent_id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return new McpAgentDeleteResponse(false, McpAdminToolFailureCodes.AgentNotFound, "Agent not found.");
            }

            var deleted = await _agentDefinitionService.DeleteAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            return deleted
                ? new McpAgentDeleteResponse(true)
                : new McpAgentDeleteResponse(false, McpAdminToolFailureCodes.AgentNotFound, "Agent not found.");
        }, static response => !response.Deleted).ConfigureAwait(false);
    }
}
