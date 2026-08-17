namespace XE_Local_AI_Engine.Client.Services.Capacity.Tools.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Server-side <see cref="IClientLocalToolHandler" /> for <c>spawn_subagent</c>. Despite the <c>ClientLocal</c>
///     location label (the offer-DTO surface), this executes ENTIRELY on the node inside the agent's
///     function-invocation pipeline — JSON-in / JSON-out, no client round-trip — which is why a spawn (capacity gate +
///     supervisor + inner <see cref="Microsoft.Agents.AI.ChatClientAgent" />) can run here at all. It resolves
///     the scoped <see cref="ISubAgentSpawnService" /> from a FRESH DI scope per call (the handler itself is a Singleton,
///     captured by <c>ClientLocalToolRegistry</c> at construction, so it cannot hold a scoped dependency directly).
/// </summary>
internal sealed class SpawnSubAgentToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IServiceScopeFactory _scopeFactory;

    public SpawnSubAgentToolHandler(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public string ToolName => SpawnSubAgentToolDefinition.ToolName;

    public string Description => SpawnSubAgentToolDefinition.Description;

    public string ParameterSchema => SpawnSubAgentToolDefinition.ParameterSchema;

    /// <summary>
    ///     Auto-execute: the spawn is gated by the capacity service + caps, not a per-call approval prompt. A declined
    ///     spawn returns a sanitized reason as the tool result, so the model recovers without a human in the loop.
    /// </summary>
    public bool RequiresApproval => false;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (jsonArguments.Length > SpawnSubAgentToolDefinition.MaxJsonArgumentsLength)
        {
            return $"spawn_subagent argument payload exceeded the maximum length of {SpawnSubAgentToolDefinition.MaxJsonArgumentsLength} characters.";
        }

        SubAgentSpawnRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SubAgentSpawnRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return $"spawn_subagent arguments were not valid JSON: {exception.Message}";
        }

        if (request is null)
        {
            return "spawn_subagent arguments were empty.";
        }

        var validationError = ValidateStringBounds(request);
        if (validationError is not null)
        {
            return validationError;
        }

        // Resolve the scoped spawn service from a fresh scope. CapacityService (scoped) depends only on singletons, so a
        // per-call scope is safe; SpawnContext is ambient (AsyncLocal, seeded by the root tool loop), not scope-bound.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var spawnService = scope.ServiceProvider.GetRequiredService<ISubAgentSpawnService>();

        return await spawnService.SpawnAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string? ValidateStringBounds(SubAgentSpawnRequest request)
    {
        if (Exceeds(request.SubAgentKey, SpawnSubAgentToolDefinition.BindingMaxLength))
        {
            return Exceeded("subAgentKey", SpawnSubAgentToolDefinition.BindingMaxLength);
        }

        if (Exceeds(request.ModelId, SpawnSubAgentToolDefinition.BindingMaxLength))
        {
            return Exceeded("modelId", SpawnSubAgentToolDefinition.BindingMaxLength);
        }

        if (Exceeds(request.Task, SpawnSubAgentToolDefinition.TaskMaxLength))
        {
            return Exceeded("task", SpawnSubAgentToolDefinition.TaskMaxLength);
        }

        return Exceeds(request.Instructions, SpawnSubAgentToolDefinition.InstructionsMaxLength)
            ? Exceeded("instructions", SpawnSubAgentToolDefinition.InstructionsMaxLength)
            : null;
    }

    private static bool Exceeds(string? value, int maximumLength) => value is { Length: > 0 } && value.Length > maximumLength;

    private static string Exceeded(string argumentName, int maximumLength) =>
        $"spawn_subagent argument '{argumentName}' exceeded the maximum length of {maximumLength} characters.";
}
