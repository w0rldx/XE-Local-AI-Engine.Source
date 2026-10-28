namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The shared nvidia-smi compute-capability parser behind the llama.cpp and whisper.cpp CUDA source builds.</summary>
[Category(TestCategories.Unit)]
public sealed class SourceBuildPolicyTests
{
    [Test]
    [Arguments("12.0", "120")]
    [Arguments("8.9", "89")]
    [Arguments("8.6\n8.9", "86;89")]
    [Arguments("8.9\n7.5\n12.0\n8.9\n", "75;89;120")]
    public void ParseCudaArchitectures_DeduplicatesAndSortsStrictCapabilities(string output, string expected)
    {
        AssertEx.Equal(expected, SourceBuildPolicy.ParseCudaArchitectures(output));
    }

    [Test]
    [Arguments("")]
    [Arguments("native")]
    [Arguments("not a capability")]
    [Arguments("4.9")]
    [Arguments("8.10")]
    [Arguments("8.9 extra")]
    [Arguments("99.9")]
    [Arguments("999")]
    public void ParseCudaArchitectures_InvalidOrOutOfBounds_FallsBack(string output)
    {
        // An unrecognised or unsupported capability falls back to the conservative set rather than failing the build:
        // a driver probe that cannot answer is not a reason to refuse to compile.
        AssertEx.Equal(SourceBuildPolicy.DefaultCudaArchitectures, SourceBuildPolicy.ParseCudaArchitectures(output));
    }
}
