namespace XE_Local_AI_Engine.Client.Services.Compute.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>run_python</c> (ClientLocal). The bridge is JSON-in / JSON-out, so
///     this handler deserializes the model arguments, validates them against the compute tool constraints, honors
///     cancellation, and delegates to <see cref="IComputeToolGateway" />. The node kill-switch
///     (<c>Compute:Enabled</c>, off by default) short-circuits before any venv or sandbox work, so a node that has not
///     opted in never provisions an interpreter, let alone runs one.
/// </summary>
internal sealed class RunPythonToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly bool _computeEnabled;
    private readonly IComputeToolGateway _gateway;

    public RunPythonToolHandler(IConfiguration configuration, IComputeToolGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _computeEnabled = configuration.GetValue<bool>("Compute:Enabled");
    }

    public string ToolName => ComputeToolDefinition.ToolName;

    public string Description => ComputeToolDefinition.Description;

    public string ParameterSchema => ComputeToolDefinition.ParameterSchema;

    // Executing model-authored code is the most consequential thing a local tool can do, so it takes the same
    // out-of-stream approval round-trip as run_in_agent_home. An operator who wants it unattended loosens it through
    // the existing per-node effective-approval policy rather than through a second mechanism here.
    public bool RequiresApproval => true;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (!_computeEnabled)
        {
            return "The Python compute tool is disabled on this node (Compute:Enabled=false).";
        }

        cancellationToken.ThrowIfCancellationRequested();

        ComputeRunToolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ComputeRunToolRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return $"run_python arguments were not valid JSON: {exception.Message}";
        }

        if (request is null)
        {
            return "run_python arguments were empty.";
        }

        var validationErrors = ComputeRunToolRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return $"run_python arguments are invalid: {string.Join(" ", validationErrors)}";
        }

        return await _gateway.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
