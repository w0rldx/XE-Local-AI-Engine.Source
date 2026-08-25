namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Fail-closed validation for <see cref="LocalContainerOptions" />. The process sandbox bounds its whole-file
///     copy-into transfer by <see cref="LocalContainerOptions.MaxCopyFileBytes" />, so that ceiling must be positive.
///     <see cref="LocalContainerOptions.MaxJailDiskBytes" /> is deliberately NOT required to be positive: a
///     non-positive value is the documented way to disable the jail disk watchdog, so rejecting it here would remove a
///     supported configuration rather than catch a mistake.
///     <para>
///         <see cref="SandboxToolchainLimits" /> is the opposite case and is floored rather than merely required to be
///         positive. On Linux those numbers become <c>MemoryMax</c> with swap denied and <c>TasksMax</c> counting
///         threads, so a value that is positive but too small does not slow a build down — it OOM-kills it, or fails
///         its first <c>fork</c>, on every attempt. A ceiling that can never be met is worse than no ceiling, because
///         it reads as protection. Unset is not rejected: unset means "derive from this host".
///     </para>
/// </summary>
public sealed class LocalContainerOptionsValidator : IValidateOptions<LocalContainerOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalContainerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var limits = options.ToolchainLimits;
        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.MaxCopyFileBytes <= 0,
                                   "LocalContainer:MaxCopyFileBytes must be greater than zero.")
                               .AppendIf(limits.CpuCount is <= 0,
                                   "LocalContainer:ToolchainLimits:CpuCount must be greater than zero when set.")
                               .AppendIf(limits.MemoryMb is not null && limits.MemoryMb < SandboxToolchainLimits.MinimumMemoryMb,
                                   $"LocalContainer:ToolchainLimits:MemoryMb must be at least {SandboxToolchainLimits.MinimumMemoryMb} MB when set; "
                                   + "a smaller ceiling OOM-kills a toolchain command rather than bounding it. Leave it unset to derive it from this host.")
                               .AppendIf(limits.PidsLimit is not null && limits.PidsLimit < SandboxToolchainLimits.MinimumPidsLimit,
                                   $"LocalContainer:ToolchainLimits:PidsLimit must be at least {SandboxToolchainLimits.MinimumPidsLimit} when set; "
                                   + "it counts THREADS, and a parallel build exceeds far less than that. Leave it unset to derive it from this host.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
