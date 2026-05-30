namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalContainerOptionsValidatorTests
{
    [Test]
    public void Validate_WithDefaults_Succeeds()
    {
        var result = Validate(new LocalContainerOptions());

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void Validate_WithRestrictedNetwork_Succeeds()
    {
        var result = Validate(new LocalContainerOptions { NetworkMode = "restricted" });

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void Validate_WithUnknownNetworkMode_Fails()
    {
        var result = Validate(new LocalContainerOptions { NetworkMode = "host" });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "NetworkMode");
    }

    [Test]
    public void Validate_WithBlankImage_Fails()
    {
        var result = Validate(new LocalContainerOptions { DefaultImage = "  " });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "DefaultImage");
    }

    [Test]
    public void Validate_WithBlankContainerNamePrefix_Fails()
    {
        var result = Validate(new LocalContainerOptions { ContainerNamePrefix = string.Empty });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "ContainerNamePrefix");
    }

    [Test]
    public void Validate_WithNonPositiveCpuLimit_Fails()
    {
        var result = Validate(new LocalContainerOptions { CpuLimit = 0 });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "CpuLimit");
    }

    [Test]
    public void Validate_WithNonPositiveMemoryLimit_Fails()
    {
        var result = Validate(new LocalContainerOptions { MemoryLimitMb = 0 });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "MemoryLimitMb");
    }

    [Test]
    public void Validate_WithNonPositivePidsLimit_Fails()
    {
        var result = Validate(new LocalContainerOptions { PidsLimit = -1 });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "PidsLimit");
    }

    [Test]
    public void Validate_WithNonPositiveMaxCopyFileBytes_Fails()
    {
        var result = Validate(new LocalContainerOptions { MaxCopyFileBytes = 0 });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "MaxCopyFileBytes");
    }

    private static ValidateOptionsResult Validate(LocalContainerOptions options)
    {
        return new LocalContainerOptionsValidator().Validate(Options.DefaultName, options);
    }
}
