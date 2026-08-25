namespace XE_Local_AI_Engine.Tests.Configuration;

using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ComputeOptionsValidatorTests
{
    private readonly ComputeOptionsValidator _validator = new();

    [Test]
    public void ComputeOptions_DefaultToDisabled()
    {
        // The whole fail-closed posture rests on this one default. It is what RunPythonToolHandler reads, and a node
        // whose configuration was stripped, defaulted, or never mentioned Compute at all must not be able to execute
        // model-authored code. Nothing else in the stack re-checks it, so a "true" here would arm the tool everywhere.
        AssertEx.False(new ComputeOptions().Enabled, "the compute tool must be off unless a node explicitly opts in");
    }

    [Test]
    public void Validate_WhenOptionsAreValid_ReturnsSuccess()
    {
        var result = _validator.Validate(name: null, new ComputeOptions());

        AssertEx.False(result.Failed);
    }

    [Test]
    public void Validate_WhenTimeoutNotPositive_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, new ComputeOptions
        {
            TimeoutSeconds = 0
        });

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Failures, failure => failure.Contains("TimeoutSeconds", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenMaxOutputBytesNotPositive_ReturnsFailure()
    {
        var result = _validator.Validate(name: null, new ComputeOptions
        {
            MaxOutputBytes = 0
        });

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaxOutputBytes", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WhenTheJailDiskCeilingIsNotPositive_ReturnsFailure()
    {
        // Zero disables the node-wide LocalContainer watchdog, but it cannot mean that here: this value only ever
        // tightens the node's ceiling, so a non-positive one is a configuration mistake rather than an opt-out.
        var result = _validator.Validate(name: null, new ComputeOptions
        {
            MaxJailDiskBytes = 0
        });

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MaxJailDiskBytes", StringComparison.Ordinal));
    }

    [Test]
    public void ComputeOptions_DefaultTheJailDiskCeilingBelowTheNodeWideOne()
    {
        // The point of the per-sandbox ceiling is that compute asks for LESS. A default at or above the node-wide one
        // would make the whole option a no-op and nothing would notice.
        AssertEx.True(new ComputeOptions().MaxJailDiskBytes < LocalContainerOptions.DefaultMaxJailDiskBytes,
            "the compute ceiling must be tighter than the node-wide default, or it changes nothing");
    }

    [Test]
    public void Validate_WhenAResourceCeilingIsNotPositive_ReturnsFailure()
    {
        // A zero ceiling is not "unlimited" — it is a request the sandbox would either reject or apply literally, so it
        // is refused at startup rather than discovered on the first tool call.
        var result = _validator.Validate(name: null,
            new ComputeOptions
            {
                MemoryMb = 0,
                CpuCount = 0,
                PidsLimit = 0
            });

        AssertEx.False(result.Succeeded);
        AssertEx.Contains(result.Failures, failure => failure.Contains("MemoryMb", StringComparison.Ordinal));
        AssertEx.Contains(result.Failures, failure => failure.Contains("CpuCount", StringComparison.Ordinal));
        AssertEx.Contains(result.Failures, failure => failure.Contains("PidsLimit", StringComparison.Ordinal));
    }
}
