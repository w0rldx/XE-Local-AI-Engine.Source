namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Fails fast at startup on an out-of-range <see cref="SpawnOptions" /> instead of letting an invalid cap throw
///     <see cref="ArgumentOutOfRangeException" /> per-invocation inside <see cref="SpawnContext.BeginRoot" /> (which
///     bounds every root agent turn). The semantics mirror the runtime guards: the fan-out cap must admit at least one
///     spawn, the cloud cap may be zero (cloud spawns disabled) but never negative, and the queue wait may be zero
///     (reject a busy same-model turn immediately) but never negative.
/// </summary>
public sealed class SpawnOptionsValidator : IValidateOptions<SpawnOptions>
{
    public ValidateOptionsResult Validate(string? name, SpawnOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(options.MaxConcurrentSpawns <= 0,
                                   "Spawn:MaxConcurrentSpawns must be greater than zero.")
                               .AppendIf(options.MaxCloudSpawns < 0,
                                   "Spawn:MaxCloudSpawns must be zero or greater.")
                               .AppendIf(options.QueueWaitSeconds < 0,
                                   "Spawn:QueueWaitSeconds must be zero or greater.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
