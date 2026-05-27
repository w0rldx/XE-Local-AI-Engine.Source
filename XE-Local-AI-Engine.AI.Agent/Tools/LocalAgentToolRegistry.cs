namespace XE_Local_AI_Engine.AI.Agent.Tools;

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;

internal sealed class LocalAgentToolRegistry : IAgentToolRegistry
{
    // Every catalog tool ships auto-execute for this RC. The flag is still surfaced per-tool so a future
    // approval flow can opt individual tools in without changing how they are sourced or executed.
    private const bool CatalogRequiresApproval = false;

    private static readonly IReadOnlyList<AITool> Tools = BuildTools();
    private static readonly IReadOnlyList<LocalChatToolDescriptor> Descriptors = BuildDescriptors(Tools);

    public IReadOnlyList<AITool> GetLocalChatTools()
    {
        return Tools;
    }

    public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
    {
        return Descriptors;
    }

    private static IReadOnlyList<AITool> BuildTools()
    {
        return
        [
            AIFunctionFactory.Create(GetCurrentTime),
            AIFunctionFactory.Create(Calculate)
        ];
    }

    private static IReadOnlyList<LocalChatToolDescriptor> BuildDescriptors(IReadOnlyList<AITool> tools)
    {
        // Derive the offer-list schema FROM each generated AIFunction so the offered contract can never drift from
        // what the factory executes.
        return
        [
            .. tools.OfType<AIFunction>()
                    .Select(static function => new LocalChatToolDescriptor(
                        function.Name,
                        function.Description,
                        function.JsonSchema.GetRawText(),
                        CatalogRequiresApproval))
        ];
    }

    [Description("Returns the current UTC time, the local time, and today's date. Use it whenever the user asks what time or what day it is.")]
    private static string GetCurrentTime(
        [Description("Optional IANA or Windows time-zone identifier (for example 'Europe/Berlin'). When omitted or unknown, the server's local time zone is used.")]
        string? timezone = null)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var (zone, zoneResolved) = ResolveTimeZone(timezone);
        var zonedNow = TimeZoneInfo.ConvertTime(utcNow, zone);

        var zoneLabel = zoneResolved
            ? zone.Id
            : $"{zone.Id} (requested zone '{timezone}' was not recognized)";

        return string.Create(CultureInfo.InvariantCulture,
            $"UTC time: {utcNow:yyyy-MM-dd HH:mm:ss} UTC. " +
            $"Local time: {zonedNow:yyyy-MM-dd HH:mm:ss} ({zoneLabel}). " +
            $"Today's date: {zonedNow:dddd, dd MMMM yyyy}.");
    }

    [Description("Evaluates a basic arithmetic expression using +, -, *, / and parentheses, then returns the numeric result. Use it for any calculation the user asks for.")]
    private static string Calculate(
        [Description("The arithmetic expression to evaluate, for example '12 * 9' or '(3 + 4) / 2'. Only numbers and the operators + - * / ( ) are allowed.")]
        string expression)
    {
        if (!ArithmeticExpressionEvaluator.TryEvaluate(expression, out var result))
        {
            return $"Unable to evaluate '{expression}'. Provide a basic arithmetic expression using only numbers and the operators + - * / ( ).";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{expression} = {result:0.############}");
    }

    private static (TimeZoneInfo Zone, bool Resolved) ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return (TimeZoneInfo.Local, true);
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(timezone.Trim(), out var zone))
        {
            return (zone, true);
        }

        return (TimeZoneInfo.Local, false);
    }
}
