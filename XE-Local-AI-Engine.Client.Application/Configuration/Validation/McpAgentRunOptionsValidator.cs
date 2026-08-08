namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;

/// <summary>Rejects lifecycle settings that weaken the approved MCP run bounds.</summary>
public sealed class McpAgentRunOptionsValidator : IValidateOptions<McpAgentRunOptions>
{
    public ValidateOptionsResult Validate(string? name, McpAgentRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.MaxConcurrentWorkers is < 1 or > 4,
                                   "Mcp:AgentRuns:MaxConcurrentWorkers must be between 1 and 4.")
                               .AppendIf(options.WatchdogMinutes is < 1 or > 60,
                                   "Mcp:AgentRuns:WatchdogMinutes must be between 1 and 60.")
                               .AppendIf(options.PollIntervalMilliseconds is < 50 or > 5000,
                                   "Mcp:AgentRuns:PollIntervalMilliseconds must be between 50 and 5000.")
                               .AppendIf(options.CompactionIntervalMinutes is < 1 or > 60,
                                   "Mcp:AgentRuns:CompactionIntervalMinutes must be between 1 and 60.")
                               .AppendIf(options.MaxTaskUtf8Bytes != 32 * 1024,
                                   "Mcp:AgentRuns:MaxTaskUtf8Bytes must remain 32768.")
                               .AppendIf(options.MaxInstructionsUtf8Bytes != 16 * 1024,
                                   "Mcp:AgentRuns:MaxInstructionsUtf8Bytes must remain 16384.")
                               .AppendIf(options.MaxResultCharacters != 24_000,
                                   "Mcp:AgentRuns:MaxResultCharacters must remain 24000.")
                               .AppendIf(options.DefaultListLimit is < 1 or > 50,
                                   "Mcp:AgentRuns:DefaultListLimit must be between 1 and 50.")
                               .AppendIf(options.MaxListLimit != 50,
                                   "Mcp:AgentRuns:MaxListLimit must remain 50.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
