namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The bring-your-own override is built from the process environment and nothing else. These cases pin the three
///     decisions that channel makes: unset means inactive, an unrecognised backend token fails fast at startup rather
///     than silently defaulting, and the recognised tokens parse case-insensitively.
/// </summary>
/// <remarks>
///     Both variables are process-global, so every case carries the keyed <c>[NotInParallel]</c> for its variable and
///     restores the previous value in a <c>finally</c>, including on failure — a leaked variable poisons every later
///     test in the module in a way that reads as an unrelated failure.
/// </remarks>
[NotInParallel(nameof(WhisperServerRuntimeOverrideOptions))]
public sealed class WhisperServerRuntimeOverrideOptionsTests
{
    [Test]
    public void FromEnvironment_UnsetPath_IsInactive()
    {
        using var environment = new OverrideEnvironment(serverPath: null, backend: null);

        var options = WhisperServerRuntimeOverrideOptions.FromEnvironment();

        AssertEx.False(options.IsActive, "With no server path configured the override must report inactive.");
        AssertEx.Null(options.ServerPath);
    }

    [Test]
    public void FromEnvironment_UnrecognisedBackend_Throws()
    {
        using var environment = new OverrideEnvironment("/opt/whisper/whisper-server", "vulkan");

        var exception = AssertEx.Throws<InvalidOperationException>(() => WhisperServerRuntimeOverrideOptions.FromEnvironment());

        AssertEx.Contains(exception.Message,
            WhisperServerRuntimeOverrideOptions.BackendEnvironmentVariable,
            StringComparison.Ordinal,
            "The failure must name the variable the operator has to fix.");
    }

    [Test]
    [Arguments("cpu", WhisperBackend.Cpu)]
    [Arguments("CPU", WhisperBackend.Cpu)]
    [Arguments("  Cpu  ", WhisperBackend.Cpu)]
    [Arguments("cuda", WhisperBackend.Cuda)]
    [Arguments("CuDa", WhisperBackend.Cuda)]
    public void FromEnvironment_CpuToken_ParsesCaseInsensitively(string token, WhisperBackend expected)
    {
        using var environment = new OverrideEnvironment("/opt/whisper/whisper-server", token);

        var options = WhisperServerRuntimeOverrideOptions.FromEnvironment();

        AssertEx.True(options.IsActive);
        AssertEx.Equal("/opt/whisper/whisper-server", options.ServerPath);
        AssertEx.Equal(expected, options.Backend);
    }

    [Test]
    public void FromEnvironment_PathSetWithNoBackend_DefaultsToCuda()
    {
        // The primary bring-your-own case is a Linux CUDA build, which upstream ships no prebuilt for.
        using var environment = new OverrideEnvironment("/opt/whisper/whisper-server", backend: null);

        var options = WhisperServerRuntimeOverrideOptions.FromEnvironment();

        AssertEx.Equal(WhisperBackend.Cuda, options.Backend);
    }

    /// <summary>Sets both override variables for one test and restores whatever was there before, including on failure.</summary>
    private sealed class OverrideEnvironment : IDisposable
    {
        private readonly string? _previousBackend;
        private readonly string? _previousServerPath;

        public OverrideEnvironment(string? serverPath, string? backend)
        {
            _previousServerPath = Environment.GetEnvironmentVariable(WhisperServerRuntimeOverrideOptions.ServerPathEnvironmentVariable);
            _previousBackend = Environment.GetEnvironmentVariable(WhisperServerRuntimeOverrideOptions.BackendEnvironmentVariable);
            Environment.SetEnvironmentVariable(WhisperServerRuntimeOverrideOptions.ServerPathEnvironmentVariable, serverPath);
            Environment.SetEnvironmentVariable(WhisperServerRuntimeOverrideOptions.BackendEnvironmentVariable, backend);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(WhisperServerRuntimeOverrideOptions.ServerPathEnvironmentVariable, _previousServerPath);
            Environment.SetEnvironmentVariable(WhisperServerRuntimeOverrideOptions.BackendEnvironmentVariable, _previousBackend);
        }
    }
}
