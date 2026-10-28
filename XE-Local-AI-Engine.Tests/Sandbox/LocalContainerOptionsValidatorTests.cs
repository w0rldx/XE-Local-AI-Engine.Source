namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class LocalContainerOptionsValidatorTests
{
    [Test]
    public void Validate_WithDefaults_Succeeds()
    {
        var result = Validate(new LocalContainerOptions());

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void Validate_WithPositiveMaxCopyFileBytes_Succeeds()
    {
        var result = Validate(new LocalContainerOptions
        {
            MaxCopyFileBytes = 1
        });

        AssertEx.True(result.Succeeded);
    }

    [Test]
    public void Validate_WithZeroMaxCopyFileBytes_Fails()
    {
        var result = Validate(new LocalContainerOptions
        {
            MaxCopyFileBytes = 0
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "MaxCopyFileBytes");
    }

    [Test]
    public void Validate_WithNegativeMaxCopyFileBytes_Fails()
    {
        var result = Validate(new LocalContainerOptions
        {
            MaxCopyFileBytes = -1
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "MaxCopyFileBytes");
    }

    /// <summary>
    ///     Toolchain ceilings are FLOORED, not merely positive: on Linux they become <c>MemoryMax</c> (swap denied) and
    ///     <c>TasksMax</c> (counting threads), so a tiny value OOM-kills a build or fails its first fork. Startup
    ///     rejects such a ceiling.
    /// </summary>
    [Test]
    [Arguments(SandboxToolchainLimits.MinimumMemoryMb - 1, null, "MemoryMb")]
    [Arguments(64, null, "MemoryMb")]
    [Arguments(null, SandboxToolchainLimits.MinimumPidsLimit - 1, "PidsLimit")]
    [Arguments(null, 64, "PidsLimit")]
    public void Validate_WithAToolchainCeilingBelowItsFloor_Fails(int? memoryMb, int? pidsLimit, string expectedKey)
    {
        var result = Validate(new LocalContainerOptions
        {
            ToolchainLimits = new SandboxToolchainLimits
            {
                MemoryMb = memoryMb,
                PidsLimit = pidsLimit
            }
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "LocalContainer:ToolchainLimits:" + expectedKey);
    }

    [Test]
    public void Validate_WithNonPositiveToolchainCpuCount_Fails()
    {
        var result = Validate(new LocalContainerOptions
        {
            ToolchainLimits = new SandboxToolchainLimits
            {
                CpuCount = 0
            }
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(result.FailureMessage, "LocalContainer:ToolchainLimits:CpuCount");
    }

    /// <summary>
    ///     Unset is not a violation — it is the instruction to derive from the host — and an override AT the floor is
    ///     accepted, so the boundary is inclusive rather than off by one.
    /// </summary>
    [Test]
    public void Validate_WithUnsetOrAtFloorToolchainCeilings_Succeeds()
    {
        AssertEx.True(Validate(new LocalContainerOptions
        {
            ToolchainLimits = new SandboxToolchainLimits()
        }).Succeeded);

        AssertEx.True(Validate(new LocalContainerOptions
        {
            ToolchainLimits = new SandboxToolchainLimits
            {
                CpuCount = 1,
                MemoryMb = SandboxToolchainLimits.MinimumMemoryMb,
                PidsLimit = SandboxToolchainLimits.MinimumPidsLimit
            }
        }).Succeeded);
    }

    private static ValidateOptionsResult Validate(LocalContainerOptions options)
    {
        return new LocalContainerOptionsValidator().Validate(Options.DefaultName, options);
    }
}
