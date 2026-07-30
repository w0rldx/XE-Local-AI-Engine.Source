namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     Fail-loud startup guard for the Development Mode sandbox provider name. Mirrors
///     <see cref="SandboxOptionsValidator" />'s shape, but guards the opposite failure: an unset value here is
///     legitimate (it means "use whatever the agent role resolved"), while a MISSPELLED one is not — and without this
///     it would surface as an <see cref="InvalidOperationException" /> from the DI factory at the first Development
///     attempt, i.e. under a user action, long after the config was edited. Wired with <c>ValidateOnStart</c>.
/// </summary>
public sealed class DevelopmentSandboxOptionsValidator : IValidateOptions<DevelopmentSandboxOptions>
{
    private static readonly string[] KnownProviders =
    [
        FakeSandboxRuntimeProvider.Name,
        ProcessSandboxRuntimeProvider.Name,
        DockerSandboxRuntimeProvider.Name
    ];

    public ValidateOptionsResult Validate(string? name, DevelopmentSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Provider))
        {
            return ValidateOptionsResult.Success;
        }

        if (!KnownProviders.Contains(options.Provider, StringComparer.Ordinal))
        {
            return ValidateOptionsResult.Fail($"{DevelopmentSandboxOptions.SectionName}:{nameof(DevelopmentSandboxOptions.Provider)} "
                                              + $"is '{options.Provider}', which is not a known sandbox provider. "
                                              + $"Expected one of: {string.Join(", ", KnownProviders)}.");
        }

        return ValidateOptionsResult.Success;
    }
}
