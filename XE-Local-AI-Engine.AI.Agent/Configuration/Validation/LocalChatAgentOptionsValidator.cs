namespace XE_Local_AI_Engine.AI.Agent.Configuration.Validation;

using Microsoft.Extensions.Options;

internal sealed class LocalChatAgentOptionsValidator : IValidateOptions<LocalChatAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalChatAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.AgentName))
        {
            errors.Add("Agent:LocalChat:AgentName is required.");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultModel))
        {
            errors.Add("Agent:LocalChat:DefaultModel is required.");
        }

        if (string.IsNullOrWhiteSpace(options.InstructionsResource))
        {
            errors.Add("Agent:LocalChat:InstructionsResource is required.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
