namespace XE_Local_AI_Engine.AI.Agent.Configuration.Validation;

using Microsoft.Extensions.Options;

internal sealed class InvocationAgentOptionsValidator : IValidateOptions<InvocationAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, InvocationAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.AgentNamePrefix))
        {
            errors.Add("Agent:Invocation:AgentNamePrefix is required.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
