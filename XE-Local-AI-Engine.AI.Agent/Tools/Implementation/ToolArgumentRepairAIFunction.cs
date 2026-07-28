namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     A <see cref="DelegatingAIFunction" /> that guards the inner executable with uniform argument validation and a
///     model-actionable repair loop. Before the handler runs it coerces and validates the model's arguments against the
///     tool's own schema (via <see cref="ToolArgumentValidator" />); a failing call — or a handler that cannot parse
///     otherwise valid-looking arguments — returns a structured repair result (via <see cref="ToolArgumentRepairResult" />)
///     instead of throwing, so the framework's function-invocation loop becomes the repair loop. A per-request cap
///     (<see cref="ToolArgumentRepairScope" />) stops a looping model: after the configured number of consecutive invalid
///     calls the tool returns a terminal "disabled for this run" result so it cannot burn the whole iteration budget.
///     The wrapper is transparent to name/description/schema (delegated to the inner function), so it composes beneath the
///     result-budget and approval wrappers without changing what the model is offered. The <c>rejectUnknownProperties</c>
///     constructor flag selects the validator's strictness: the app's own tools pass <c>true</c> (undeclared keys are hallucinations to
///     reject); third-party MCP tools pass <c>false</c> so an under-declared server schema never bounces a key the tool
///     actually needs (required/type checks still apply).
/// </summary>
internal sealed class ToolArgumentRepairAIFunction : DelegatingAIFunction
{
    private static readonly Meter Meter = new("XE.LocalAiEngine.AI.Agent", "1.0.0");

    private static readonly Counter<long> RepairCounter = Meter.CreateCounter<long>(
        "xe.agent.tool_argument_repair",
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
            // The arguments passed structural validation but the handler could not deserialize them into the shape it
            // needs. Surface it as a model-actionable repair (without echoing the raw payload) rather than letting the
            // throw become an opaque framework error that counts toward the run's abort threshold.
            RecordRepair("handler_json");
            return RecordInvalidAndBuildResult(scope, "The tool could not parse the supplied arguments; they do not match the expected shape.");
        }
    }

    private static void RecordRepair(string source)
    {
        // Deliberately content-free: no tool name, argument key/value, schema, model, request, or user dimension.
        RepairCounter.Add(1, new KeyValuePair<string, object?>("source", source));
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
