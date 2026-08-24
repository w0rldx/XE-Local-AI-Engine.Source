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
                               // Zero would mean pinning every numeric library to no threads at all, which is not a
                               // posture. The sandbox create request rejects a non-positive thread limit as well, but
                               // failing at startup names the setting; failing at the first tool call would not.
                               .AppendIf(options.ThreadLimit <= 0,
                                   "Compute:ThreadLimit must be greater than zero.")
                               // Unlike LocalContainer:MaxJailDiskBytes, a non-positive value is NOT a supported way to
                               // disable the watchdog here: this ceiling can only tighten the node-wide one, so zero
                               // would silently mean "inherit" rather than "unlimited" and read as the opposite.
                               .AppendIf(options.MaxJailDiskBytes <= 0,
                                   "Compute:MaxJailDiskBytes must be greater than zero.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
