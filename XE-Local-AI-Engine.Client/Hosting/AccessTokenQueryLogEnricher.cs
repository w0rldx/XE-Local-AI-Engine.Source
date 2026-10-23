namespace XE_Local_AI_Engine.Client.Hosting;

using Serilog.Core;
using Serilog.Events;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Redacts <c>access_token</c> in every log event's <c>QueryString</c> property, so the SignalR JWT a browser sends
///     in the query never reaches a sink.
/// </summary>
/// <remarks>
///     The framework's <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> "Request starting" and "Request finished" events
///     carry the raw query in <c>QueryString</c>; they show once an operator lowers that category to Information. Their
///     rendered message is built from the property, so rewriting the property is enough. The Serilog request-completion
///     line is already redacted in <c>Program.ConfigureRequestLogging</c>; redacting it again is a no-op.
/// </remarks>
internal sealed class AccessTokenQueryLogEnricher : ILogEventEnricher
{
    private const string QueryStringProperty = "QueryString";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (!logEvent.Properties.TryGetValue(QueryStringProperty, out var value) || value is not ScalarValue { Value: string query })
        {
            return;
        }

        var redacted = AccessTokenQueryRedactor.Redact(query);
        if (!string.Equals(redacted, query, StringComparison.Ordinal))
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(QueryStringProperty, new ScalarValue(redacted)));
        }
    }
}
