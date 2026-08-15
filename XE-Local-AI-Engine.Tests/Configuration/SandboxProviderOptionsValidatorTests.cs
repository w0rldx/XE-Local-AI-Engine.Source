namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Hosting;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two sandbox-provider startup guards, which look alike and guard opposite failures.
///     <see cref="SandboxOptionsValidator" /> refuses to start Production with an UNSET provider (there is no
///     execution-capable default, and silently falling back would hand a stripped config the host-command-executing
///     provider), while <see cref="DevelopmentSandboxOptionsValidator" /> accepts an unset value and rejects a
///     MISSPELLED one — which would otherwise only surface as a DI failure under a user action, long after the edit.
/// </summary>
public sealed class SandboxProviderOptionsValidatorTests
{
    [Test]
    public void SandboxValidator_InProductionWithNoProvider_Throws()
    {
        var validator = new SandboxOptionsValidator(Environment("Production"));

        var exception = AssertEx.Throws<InvalidOperationException>(() => validator.Validate(name: null, new SandboxOptions()));

        AssertEx.Contains(exception.Message, "must be set in Production");
        AssertEx.Contains(exception.Message, SandboxOptions.SectionName);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void SandboxValidator_OutsideProductionWithNoProvider_ReturnsSuccess(string? provider)
    {
        // Non-Production resolves the deterministic fake, so an unset provider is legitimate there.
        var validator = new SandboxOptionsValidator(Environment("Development"));

        var result = validator.Validate(name: null, new SandboxOptions
        {
            Provider = provider
        });

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void SandboxValidator_InProductionWithAProvider_ReturnsSuccess()
    {
        var validator = new SandboxOptionsValidator(Environment("Production"));

        var result = validator.Validate(name: null, new SandboxOptions
        {
            Provider = "docker"
        });

        AssertEx.True(result.Succeeded);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("  ")]
    public void DevelopmentSandboxValidator_WithNoProvider_ReturnsSuccess(string? provider)
    {
        // Unset here means "use whatever the agent role resolved" — it must not block startup.
        var result = new DevelopmentSandboxOptionsValidator().Validate(name: null, new DevelopmentSandboxOptions
        {
            Provider = provider
        });

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void DevelopmentSandboxValidator_WithAnUnknownProvider_ReturnsFailureNamingTheAllowedSet()
    {
        var result = new DevelopmentSandboxOptionsValidator().Validate(name: null, new DevelopmentSandboxOptions
        {
            Provider = "dokcer"
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "dokcer");
        AssertEx.Contains(result.FailureMessage, "not a known sandbox provider");
        AssertEx.Contains(result.FailureMessage, "Expected one of:");
    }

    [Test]
    public void DevelopmentSandboxValidator_ComparesTheProviderNameOrdinally()
    {
        // The DI factory keys on the exact name, so a case variant is a misconfiguration and must be caught here rather
        // than at the first Development attempt.
        var result = new DevelopmentSandboxOptionsValidator().Validate(name: null, new DevelopmentSandboxOptions
        {
            Provider = "DOCKER"
        });

        AssertEx.True(result.Failed);
    }

    private static IHostEnvironment Environment(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName = environmentName;
        return environment;
    }
}
