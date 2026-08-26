namespace XE_Local_AI_Engine.Client.Services.Compute.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>run_python</c> (ClientLocal). The bridge is JSON-in / JSON-out, so
///     this handler deserializes the model arguments, honors cancellation, and delegates to
///     <see cref="IComputeToolGateway" />.
///     <para>
///         It deliberately holds NO copy of the node kill-switch and NO copy of the request validation: both live in
///         <see cref="IComputeToolGateway.ExecuteDetailedAsync" />, which every caller goes through. While they lived
///         here they were properties of this handler rather than of the sandbox, so a second caller of the gateway got
///         neither. The model-facing sentences are unchanged — they moved with the checks.
///     </para>
/// </summary>
internal sealed class RunPythonToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IComputeToolGateway _gateway;

    public RunPythonToolHandler(IComputeToolGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
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
        cancellationToken.ThrowIfCancellationRequested();

        // The one thing that IS this handler's own: turning a JSON envelope into the typed request. Everything the
        // request then has to satisfy is the gateway's.
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

        return await _gateway.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
