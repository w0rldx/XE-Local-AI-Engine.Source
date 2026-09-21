namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Contracts.Telemetry;

/// <summary>
///     A <see cref="DelegatingAIFunction" /> that guards the inner executable with uniform argument validation and a
///     model-actionable repair loop.
/// </summary>
/// <remarks>
///     A failing call — or a handler that cannot parse otherwise valid-looking arguments — returns a structured repair
///     result instead of throwing, so the function-invocation loop becomes the repair loop, and
///     <see cref="ToolArgumentRepairScope" />'s per-request cap disables a looping tool before it burns the iteration
///     budget. Transparent to name, description and schema. <c>rejectUnknownProperties</c> is <c>true</c> for the
///     app's own tools and <c>false</c> for third-party MCP schemas that may under-declare their inputs.
/// </remarks>
internal sealed class ToolArgumentRepairAIFunction : DelegatingAIFunction
{
    private static readonly Meter Meter = new(TelemetrySourceNames.Agent, "1.0.0");

    private static readonly Counter<long> RepairCounter = Meter.CreateCounter<long>("xe.agent.tool_argument_repair",
        description: "Tool calls intercepted for model-actionable argument repair. Tag: source.");

    private readonly int _maxConsecutiveInvalidCalls;
    private readonly bool _rejectUnknownProperties;

    public ToolArgumentRepairAIFunction(AIFunction innerFunction, int maxConsecutiveInvalidCalls, bool rejectUnknownProperties = true)
        : base(innerFunction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConsecutiveInvalidCalls);
        _maxConsecutiveInvalidCalls = maxConsecutiveInvalidCalls;
        _rejectUnknownProperties = rejectUnknownProperties;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var scope = ToolArgumentRepairScope.Current;

        // Already cut off for this request: short-circuit without touching the handler.
        if (scope is not null && scope.IsDisabled(Name))
        {
            return ToolArgumentRepairResult.ToolDisabled(Name);
        }

        var validation = ToolArgumentValidator.CoerceAndValidate(JsonSchema, arguments, _rejectUnknownProperties);
        if (!validation.IsValid)
        {
            RecordRepair("validation");
            return RecordInvalidAndBuildResult(scope, validation.Reason!);
        }

        if (validation.WasCoerced)
        {
            RecordRepair("coercion");
        }

        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            scope?.RecordValidCall(Name);
            return result;
        }
        catch (JsonException)
        {
            // Structural validation passed but the handler could not deserialize the shape it needs: surface a
            // model-actionable repair, without echoing the payload, rather than an opaque framework error.
            RecordRepair("handler_json");
            return RecordInvalidAndBuildResult(scope, "The tool could not parse the supplied arguments; they do not match the expected shape.");
        }
    }

    private static void RecordRepair(string source)
    {
        // Deliberately content-free: no tool name, argument key/value, schema, model, request, or user dimension.
        RepairCounter.Add(1, new KeyValuePair<string, object?>("source", source));
        ProviderCallBudget.Current?.RecordToolArgumentRepair();
    }

    private object RecordInvalidAndBuildResult(ToolArgumentRepairScope? scope, string reason)
    {
        // Outside a request scope (e.g. a direct invocation) the cap cannot be tracked, so just return the repair guidance.
        if (scope is null)
        {
            return ToolArgumentRepairResult.InvalidArguments(reason, JsonSchema);
        }

        var attempts = scope.RecordInvalidCall(Name);
        if (attempts >= _maxConsecutiveInvalidCalls)
        {
            scope.Disable(Name);
            return ToolArgumentRepairResult.ToolDisabled(Name);
        }

        return ToolArgumentRepairResult.InvalidArguments(reason, JsonSchema);
    }
}
