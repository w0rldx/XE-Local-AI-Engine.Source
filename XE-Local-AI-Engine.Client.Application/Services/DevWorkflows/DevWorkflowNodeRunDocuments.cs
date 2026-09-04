namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;

/// <summary>
///     Reads back the two JSON documents a node run stores its routing and tool telemetry in: the route
///     <c>DevWorkflowStateMachine.RouteJson</c> writes, and the tool-name array
///     <c>DevWorkflowNodeTelemetrySource</c> writes.
///     <para>
///         Kept beside those writers rather than in the read model, for the reason
///         <see cref="DevWorkflowRulePolicyResolver.Read" /> is: a column that will not parse is a hand-edited row, and
///         whether that costs the node one field or costs the whole read a 500 is a judgement about the document, which
///         belongs to the code that owns it. A reader in an endpoint would be a second copy of that judgement, and the
///         catch it needs is exactly what the endpoint layer must not carry.
///     </para>
/// </summary>
public static class DevWorkflowNodeRunDocuments
{
    /// <summary>camelCase, matching the documents the runtime wrote into these columns.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     A stored route, or null when the column is empty or unreadable — the node then simply has no route to show.
    ///     <para>
    ///         No list is ever null on the way out, and a plain deserialize cannot promise that: a document that
    ///         omits <c>satisfied</c> lands a null list, and the generated client validates the response with zod,
    ///         which accepts a missing member and REJECTS a null one. An absent list is therefore an empty list, and a
    ///         null ELEMENT is dropped for the same reason — one bad entry costs its own key, not the whole read.
    ///     </para>
    /// </summary>
    public static DevWorkflowRoute? TryParseRoute(string? routeJson) =>
        Read<StoredRoute>(routeJson) is { } route
            ? new DevWorkflowRoute(Names(route.Satisfied), Names(route.Dead), Names(route.Waived), route.GateAnswer, route.Truncated)
            : null;

    /// <summary>
    ///     The tool names as the column stores them, or null when it is empty or unreadable — which reads as the same
    ///     "there were no step rows to count" the column's own null means. A last element of <c>"…"</c> is the writer's
    ///     truncation marker and is kept; a null element is dropped rather than shipped into an array of strings.
    /// </summary>
    public static IReadOnlyList<string>? ToolNames(string? toolNamesJson) =>
        Read<IReadOnlyList<string?>>(toolNamesJson) is { } names ? Names(names) : null;

    private static IReadOnlyList<string> Names(IReadOnlyList<string?>? names) =>
        names is null ? [] : [.. names.OfType<string>()];

    private static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The stored document, whose lists may be absent — <see cref="TryParseRoute" /> is what normalises them. A row
    ///     written before the waived bucket existed simply omits it, which reads back as the empty list it means.
    /// </summary>
    private sealed record StoredRoute(IReadOnlyList<string?>? Satisfied,
        IReadOnlyList<string?>? Dead,
        IReadOnlyList<string?>? Waived,
        string? GateAnswer,
        bool Truncated);
}
