namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     An executable <see cref="AIFunction" /> that carries an explicit, model-visible JSON schema and description
///     while keeping a JSON-in / JSON-out handler body.
///     <para>
///         <see cref="AIFunctionFactory" /> cannot attach an arbitrary raw schema to an executable (its options only
///         tune auto-generation from the delegate signature), so this type overrides the schema/name/description
///         surface directly and serializes the raw invocation arguments back to JSON for the handler. That lets a
///         single-string handler advertise the real multi-property schema without the factory mis-binding the
///         model's argument object onto a lone <c>arguments</c> parameter.
///     </para>
/// </summary>
internal sealed class MetadataToolFunction : AIFunction
{
    private readonly Func<string, CancellationToken, Task<string>> _handler;

    public MetadataToolFunction(string name,
        string? description,
        JsonElement jsonSchema,
        Func<string, CancellationToken, Task<string>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);

        Name = name;
        Description = description ?? string.Empty;
        JsonSchema = jsonSchema;
        _handler = handler;
    }

    public override string Name { get; }

    public override string Description { get; }

    public override JsonElement JsonSchema { get; }

    /// <summary>Parses a tool's <c>ParameterSchema</c> JSON string into a detached <see cref="JsonElement" />.</summary>
    public static JsonElement ParseSchema(string parameterSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterSchema);

        using var document = JsonDocument.Parse(parameterSchema);
        return document.RootElement.Clone();
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            payload[argument.Key] = argument.Value;
        }

        var json = JsonSerializer.Serialize(payload);
        return await _handler(json, cancellationToken).ConfigureAwait(false);
    }
}
