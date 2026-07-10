namespace XE_Local_AI_Engine.AI.Agent.Configuration.Validation;

using Microsoft.Extensions.Options;

internal sealed class AgentToolPipelineOptionsValidator : IValidateOptions<AgentToolPipelineOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentToolPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];

        if (options.MaximumToolIterationsPerRequest < 1)
        {
            errors.Add("Agent:ToolPipeline:MaximumToolIterationsPerRequest must be at least 1.");
        }

        if (options.MaxToolResultCharacters < 1024)
        {
            errors.Add("Agent:ToolPipeline:MaxToolResultCharacters must be at least 1024.");
        }

        if (options.MaxConsecutiveInvalidToolCallsPerTool < 1)
        {
            errors.Add("Agent:ToolPipeline:MaxConsecutiveInvalidToolCallsPerTool must be at least 1.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
