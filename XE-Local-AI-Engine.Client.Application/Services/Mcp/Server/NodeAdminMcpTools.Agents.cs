namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.ComponentModel;
using ModelContextProtocol.Server;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Agent tools of <see cref="NodeAdminMcpTools" />: reading, creating, replacing and deleting a saved agent
///     through the same validation service the operator API uses.
/// </summary>
public sealed partial class NodeAdminMcpTools
{
    [McpServerTool(Name = "get_agent", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Get one saved agent by id or exact name.")]
    // Tool parameter names are snake_case: they are MCP's public JSON contract.
    public async Task<McpAgentResponse> GetAgentAsync(string agent_id, CancellationToken cancellationToken)
    {
        return await InvokeAuditedAsync("get_agent", AuditArguments(("agent_id", agent_id)), async () =>
        {
            var (record, ambiguous) = await FindAgentAsync(agent_id, cancellationToken);
            if (ambiguous)
            {
                return AgentNameAmbiguous();
            }

            return record is null
                ? AgentNotFound()
                : new McpAgentResponse
                {
                    Status = "ok",
                    Agent = McpAgentDefinition.FromRecord(record)
                };
        }, static response => response.FailureCode is not null);
    }

    [McpServerTool(Name = "create_agent", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Validate and create a saved agent through the same application service used by the operator API.")]
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
            static response => response.FailureCode is not null);

    [McpServerTool(Name = "update_agent", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Fully replace an existing saved agent by id or exact name through the shared validation service.")]
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
            var (existing, ambiguous) = await FindAgentAsync(agent_id, cancellationToken);
            if (ambiguous)
            {
                return AgentNameAmbiguous();
            }

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
                cancellationToken);
        }, static response => response.FailureCode is not null);
    }

    [McpServerTool(Name = "delete_agent", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Delete a saved agent by id or exact name.")]
    public async Task<McpAgentDeleteResponse> DeleteAgentAsync(string agent_id, CancellationToken cancellationToken)
    {
        return await InvokeAuditedAsync("delete_agent", AuditArguments(("agent_id", agent_id)), async () =>
        {
            var (existing, ambiguous) = await FindAgentAsync(agent_id, cancellationToken);
            if (ambiguous)
            {
                return new McpAgentDeleteResponse
                {
                    Deleted = false,
                    FailureCode = McpExecutionFailureCodes.AmbiguousName,
                    DisplayMessage = AmbiguousNameMessage
                };
            }

            if (existing is null)
            {
                return new McpAgentDeleteResponse
                {
                    Deleted = false,
                    FailureCode = McpAdminToolFailureCodes.AgentNotFound,
                    DisplayMessage = "Agent not found."
                };
            }

            var deleted = await _agentDefinitionService.DeleteAsync(existing.Id, cancellationToken);
            return deleted
                ? new McpAgentDeleteResponse
                {
                    Deleted = true
                }
                : new McpAgentDeleteResponse
                {
                    Deleted = false,
                    FailureCode = McpAdminToolFailureCodes.AgentNotFound,
                    DisplayMessage = "Agent not found."
                };
        }, static response => !response.Deleted);
    }

    private const string AmbiguousNameMessage = "Several saved agents share this name. Use the agent id instead.";

    // Q5: names are not unique, so a name that matches several agents is refused instead of acting on the first row (I-D6).
    private async Task<(AgentDefinitionRecord? Record, bool Ambiguous)> FindAgentAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return (await _agentDefinitionService.GetByKeyAsync(key, cancellationToken), false);
        }
        catch (AgentDefinitionAmbiguousNameException)
        {
            return (null, true);
        }
    }

    private static McpAgentResponse AgentNameAmbiguous() =>
        new()
        {
            Status = "ambiguous",
            Agent = null,
            FailureCode = McpExecutionFailureCodes.AmbiguousName,
            DisplayMessage = AmbiguousNameMessage
        };
}
