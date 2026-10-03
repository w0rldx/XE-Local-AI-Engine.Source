namespace XE_Local_AI_Engine.Client.Services.Diagnostics;

using Serilog.Core;
using Serilog.Events;

/// <summary>
///     The session-only verbose-logging switch the host's Serilog pipeline is <c>ControlledBy</c>: Debug until the next
///     restart, while the configured <c>Microsoft*</c>/<c>System</c> overrides still apply.
/// </summary>
public sealed class NodeLogLevelSwitch
{
    private readonly LogEventLevel _configured;

    public NodeLogLevelSwitch(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var text = configuration["Serilog:MinimumLevel:Default"] ?? configuration["Serilog:MinimumLevel"];
        _configured = Enum.TryParse<LogEventLevel>(text, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : LogEventLevel.Information;
        Level = new LoggingLevelSwitch(_configured);
    }

    public LoggingLevelSwitch Level { get; }

    public bool Verbose => Level.MinimumLevel <= LogEventLevel.Debug;

    /// <summary><see langword="true" /> lowers the minimum level to Debug; <see langword="false" /> restores the configured level.</summary>
    public void Set(bool verbose) =>
        Level.MinimumLevel = verbose && _configured > LogEventLevel.Debug ? LogEventLevel.Debug : _configured;
}
