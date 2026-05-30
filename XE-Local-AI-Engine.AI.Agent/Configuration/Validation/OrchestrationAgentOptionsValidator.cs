namespace XE_Local_AI_Engine.AI.Agent.Configuration.Validation;

using Microsoft.Extensions.Options;

internal sealed class OrchestrationAgentOptionsValidator : IValidateOptions<OrchestrationAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, OrchestrationAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];

        if (options.IdleTimeoutSeconds <= 0)
        {
            errors.Add("Agent:Orchestration:IdleTimeoutSeconds must be positive.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
