namespace XE_Local_AI_Engine.Client.Services.Coder.Tools.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>read_file</c> (ClientLocal). JSON-in / JSON-out: deserializes the
///     model arguments, validates them, and delegates to <see cref="ICoderWorkspaceReader" />. Read-only and
///     workspace-confined, so it auto-runs (<c>RequiresApproval => false</c>). Gated by
///     <c>AgentHome:Enabled</c>.
/// </summary>
internal sealed class ReadFileToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly ICoderWorkspaceReader _reader;

    public ReadFileToolHandler(INodeRuntimeSettings runtimeSettings, ICoderWorkspaceReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    }

    public string ToolName => CoderToolDefinition.ReadFileToolName;

    public string Description => CoderToolDefinition.ReadFileDescription;

    public string ParameterSchema => CoderToolDefinition.ReadFileParameterSchema;

    public bool RequiresApproval => false;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (!await _runtimeSettings.GetAgentHomeEnabledAsync(cancellationToken))
        {
            return "Agent Mode is disabled on this node (AgentHome:Enabled=false).";
        }

        cancellationToken.ThrowIfCancellationRequested();

        ReadFileToolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ReadFileToolRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return $"read_file arguments were not valid JSON: {exception.Message}";
        }

        if (request is null)
        {
            return "read_file arguments were empty.";
        }

        var validationErrors = CoderToolRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return $"read_file arguments are invalid: {string.Join(" ", validationErrors)}";
        }

        return await _reader.ReadFileAsync(request, cancellationToken);
    }
}
