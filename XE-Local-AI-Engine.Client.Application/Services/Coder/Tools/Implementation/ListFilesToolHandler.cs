namespace XE_Local_AI_Engine.Client.Services.Coder.Tools.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>list_files</c> (ClientLocal). JSON-in / JSON-out: deserializes the
///     model arguments, validates them, and delegates to <see cref="ICoderWorkspaceReader" />. Read-only and
///     workspace-confined, so it auto-runs (<c>RequiresApproval => false</c>). Gated by
///     <c>AgentHome:Enabled</c> — the coder tools share the AgentHome sandbox.
/// </summary>
internal sealed class ListFilesToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly bool _agentHomeEnabled;
    private readonly ICoderWorkspaceReader _reader;

    public ListFilesToolHandler(IConfiguration configuration, ICoderWorkspaceReader reader)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _agentHomeEnabled = configuration.GetValue<bool>("AgentHome:Enabled");
    }

    public string ToolName => CoderToolDefinition.ListFilesToolName;

    public string Description => CoderToolDefinition.ListFilesDescription;

    public string ParameterSchema => CoderToolDefinition.ListFilesParameterSchema;

    public bool RequiresApproval => false;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (!_agentHomeEnabled)
        {
            return "Agent Mode is disabled on this node (AgentHome:Enabled=false).";
        }

        cancellationToken.ThrowIfCancellationRequested();

        ListFilesToolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ListFilesToolRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return $"list_files arguments were not valid JSON: {exception.Message}";
        }

        request ??= new ListFilesToolRequest();

        var validationErrors = CoderToolRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return $"list_files arguments are invalid: {string.Join(" ", validationErrors)}";
        }

        return await _reader.ListFilesAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
