namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Mcp;

public sealed class McpOptionsValidator : IValidateOptions<McpOptions>
{
    public ValidateOptionsResult Validate(string? name, McpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.ConnectTimeoutSeconds <= 0,
                                   "Mcp:ConnectTimeoutSeconds must be greater than zero.")
                               .AppendIf(options.ToolCallTimeoutSeconds <= 0,
                                   "Mcp:ToolCallTimeoutSeconds must be greater than zero.")
                               .AppendIf(options.HttpLoopbackHosts is null || options.HttpLoopbackHosts.Count == 0,
                                   "Mcp:HttpLoopbackHosts must contain at least one allowed host.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
