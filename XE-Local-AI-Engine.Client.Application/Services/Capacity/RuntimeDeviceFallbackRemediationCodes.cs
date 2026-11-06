namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Stable codes for which CPU-fallback remediation applies. It depends on the host OS and GPU vendor, not on the reason,
///     so it is a code of its own; the operator UI localizes the remediation from it.
/// </summary>
public static class RuntimeDeviceFallbackRemediationCodes
{
    /// <summary>Windows: update the driver, check the GPU is visible, reinstall the runtime, verify any override.</summary>
    public const string Windows = "windows";

    /// <summary>Linux with an NVIDIA GPU: automatic variant selection so the managed CUDA prebuilt serves, or the Vulkan source build.</summary>
    public const string LinuxNvidia = "linux_nvidia";

    /// <summary>Linux with any other GPU: automatic variant selection and a Vulkan ICD, or the Vulkan source build.</summary>
    public const string LinuxOther = "linux_other";
}
