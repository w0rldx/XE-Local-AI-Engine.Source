namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Compute;

public sealed class ComputeOptionsValidator : IValidateOptions<ComputeOptions>
{
    public ValidateOptionsResult Validate(string? name, ComputeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.TimeoutSeconds <= 0,
                                   "Compute:TimeoutSeconds must be greater than zero.")
                               .AppendIf(options.MaxOutputBytes <= 0,
                                   "Compute:MaxOutputBytes must be greater than zero.")
                               .AppendIf(options.MemoryMb <= 0,
                                   "Compute:MemoryMb must be greater than zero.")
                               .AppendIf(options.CpuCount <= 0,
                                   "Compute:CpuCount must be greater than zero.")
                               .AppendIf(options.PidsLimit <= 0,
                                   "Compute:PidsLimit must be greater than zero.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
