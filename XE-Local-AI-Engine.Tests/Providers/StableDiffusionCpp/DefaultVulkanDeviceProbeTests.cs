namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Exercises the default Vulkan device probe's decision purely through its injected seams (explicit-ICD env, WSL,
///     ICD manifest) so no real filesystem or process environment is touched. Proves the WSL/ICD logic, the fail-safe on
///     IO errors, and that the verdict is computed once and cached.
/// </summary>
public sealed class DefaultVulkanDeviceProbeTests
{
    [Test]
    public void HasEnumerableVulkanDevice_WhenExplicitIcdEnvSet_ReturnsTrue_EvenUnderWsl()
    {
        // An explicit VK_ICD_FILENAMES is trusted on any Linux (WSL included) and short-circuits before the WSL branch.
        var probe = new DefaultVulkanDeviceProbe(hasExplicitIcdEnvironment: () => true, isWsl: () => true, hasIcdManifest: () => false);

        AssertEx.True(probe.HasEnumerableVulkanDevice());
    }

    [Test]
    public void HasEnumerableVulkanDevice_WhenWslWithNoExplicitIcd_ReturnsFalse_EvenWithManifest()
    {
        // The WSL2 gap: GPU is exposed via CUDA/dxcore, so a standard-directory manifest is not a reliable Vulkan proxy.
        var probe = new DefaultVulkanDeviceProbe(hasExplicitIcdEnvironment: () => false, isWsl: () => true, hasIcdManifest: () => true);

        AssertEx.False(probe.HasEnumerableVulkanDevice());
    }

    [Test]
    public void HasEnumerableVulkanDevice_WhenBareMetalLinuxWithManifest_ReturnsTrue()
    {
        var probe = new DefaultVulkanDeviceProbe(hasExplicitIcdEnvironment: () => false, isWsl: () => false, hasIcdManifest: () => true);

        AssertEx.True(probe.HasEnumerableVulkanDevice());
    }

    [Test]
    public void HasEnumerableVulkanDevice_WhenBareMetalLinuxWithNoManifest_ReturnsFalse()
    {
        var probe = new DefaultVulkanDeviceProbe(hasExplicitIcdEnvironment: () => false, isWsl: () => false, hasIcdManifest: () => false);

        AssertEx.False(probe.HasEnumerableVulkanDevice());
    }

    [Test]
    public void HasEnumerableVulkanDevice_WhenManifestCheckThrowsIoError_FailsSafeToFalse()
    {
        var probe = new DefaultVulkanDeviceProbe(hasExplicitIcdEnvironment: () => false,
            isWsl: () => false,
            hasIcdManifest: () => throw new IOException("simulated filesystem failure"));

        AssertEx.False(probe.HasEnumerableVulkanDevice());
    }

    [Test]
    public void HasEnumerableVulkanDevice_ComputesVerdictOnceAndCaches()
    {
        var manifestChecks = 0;
        var probe = new DefaultVulkanDeviceProbe(hasExplicitIcdEnvironment: () => false,
            isWsl: () => false,
            hasIcdManifest: () =>
            {
                manifestChecks++;
                return true;
            });

        _ = probe.HasEnumerableVulkanDevice();
        _ = probe.HasEnumerableVulkanDevice();

        AssertEx.Equal(1, manifestChecks);
    }
}
