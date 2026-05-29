namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;

public sealed class AgentHomeOptionsValidator : IValidateOptions<AgentHomeOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentHomeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.PrepareStaleAfterSeconds <= 0,
                                   "AgentHome:PrepareStaleAfterSeconds must be greater than zero.")
                               .AppendIf(options.RootPath is not null && string.IsNullOrWhiteSpace(options.RootPath),
                                   "AgentHome:RootPath must not be blank when specified.")
                               .AppendIf(string.IsNullOrWhiteSpace(options.DefaultRuntimeProfile),
                                   "AgentHome:DefaultRuntimeProfile must not be blank.")
                               .AppendIf(options.PrepareTimeoutSeconds <= 0,
                                   "AgentHome:PrepareTimeoutSeconds must be greater than zero.")
                               .AppendIf(options.CommandTimeoutSeconds <= 0,
                                   "AgentHome:CommandTimeoutSeconds must be greater than zero.")
                               .AppendIf(options.MaxSelectedFolderBytes <= 0,
                                   "AgentHome:MaxSelectedFolderBytes must be greater than zero.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
