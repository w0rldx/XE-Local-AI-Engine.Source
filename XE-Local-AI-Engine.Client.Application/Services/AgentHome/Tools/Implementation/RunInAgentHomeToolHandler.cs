namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>run_in_agent_home</c> (Option B). The bridge is JSON-in /
///     JSON-out, so this handler deserializes the model arguments, validates them against the AgentHome plan §7
///     constraints, honors cancellation, and delegates to <see cref="IAgentHomeToolGateway" />. In Marker B the
///     gateway is a pending placeholder; the tool stays off the wire (server seed <c>IsActive=false</c> +
///     <c>AgentHome:Enabled=false</c>) until Marker I.
/// </summary>
internal sealed class RunInAgentHomeToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly bool _agentHomeEnabled;
    private readonly IAgentHomeToolGateway _gateway;

    public RunInAgentHomeToolHandler(IConfiguration configuration, IAgentHomeToolGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _agentHomeEnabled = configuration.GetValue<bool>("AgentHome:Enabled");
    }

    public string ToolName => AgentHomeToolDefinition.ToolName;

    public string Description => AgentHomeToolDefinition.Description;

    public string ParameterSchema => AgentHomeToolDefinition.ParameterSchema;

    public bool RequiresApproval => true;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (!_agentHomeEnabled)
        {
            return "AgentHome is disabled on this node (AgentHome:Enabled=false).";
        }

        cancellationToken.ThrowIfCancellationRequested();

        AgentHomeRunToolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AgentHomeRunToolRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return $"run_in_agent_home arguments were not valid JSON: {exception.Message}";
        }

        if (request is null)
        {
            return "run_in_agent_home arguments were empty.";
        }

        var validationErrors = AgentHomeRunToolRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return $"run_in_agent_home arguments are invalid: {string.Join(" ", validationErrors)}";
        }

        return await _gateway.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
