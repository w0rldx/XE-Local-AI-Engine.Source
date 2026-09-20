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
                               .AppendIf(options.MaxRunSeconds <= 0,
                                   "AgentHome:MaxRunSeconds must be greater than zero.")
                               // Both configured values, not just the key names: this stops host startup, and an
                               // operator who set only one of them cannot tell which value they did not write.
                               .AppendIf(options.MaxRunSeconds < options.CommandTimeoutSeconds,
                                   $"AgentHome:MaxRunSeconds ({options.MaxRunSeconds}) must be at least AgentHome:CommandTimeoutSeconds "
                                   + $"({options.CommandTimeoutSeconds}) — the whole-run budget cannot be shorter than one command's.")
                               .AppendIf(options.MaxInnerToolCalls <= 0,
                                   "AgentHome:MaxInnerToolCalls must be greater than zero.")
                               .AppendIf(options.MaxWriteFileBytes <= 0,
                                   "AgentHome:MaxWriteFileBytes must be greater than zero.")
                               .AppendIf(options.MaxTotalWriteBytes < options.MaxWriteFileBytes,
                                   "AgentHome:MaxTotalWriteBytes must be at least AgentHome:MaxWriteFileBytes.")
                               .AppendIf(options.MaxCommandOutputBytes <= 0,
                                   "AgentHome:MaxCommandOutputBytes must be greater than zero.")
                               .AppendIf(options.MaxSelectedFolderBytes <= 0,
                                   "AgentHome:MaxSelectedFolderBytes must be greater than zero.")
                               .AppendIf(options.MaxPatchBytes <= 0,
                                   "AgentHome:MaxPatchBytes must be greater than zero.")
                               .AppendIf(options.PatchApplyTimeoutSeconds <= 0,
                                   "AgentHome:PatchApplyTimeoutSeconds must be greater than zero.")
                               .AppendIf(options.Enabled && (options.ToolCapableModels is null || options.ToolCapableModels.Count == 0),
                                   "AgentHome:ToolCapableModels must contain at least one model id when AgentHome is enabled.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
