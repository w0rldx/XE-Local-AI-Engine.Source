namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Default <see cref="IVulkanDeviceProbe" />: decides whether a Vulkan device will enumerate from cheap, host-local
///     signals only — no native Vulkan loader load, no heavy dependency. The verdict is computed once and cached for the
///     process lifetime via a thread-safe <see cref="Lazy{T}" />.
/// </summary>
/// <remarks>
///     Decided in order. <c>VK_ICD_FILENAMES</c> set → present, trusted unconditionally, because an explicit ICD path is how Vulkan is
///     configured on any Linux, WSL included (the WSL Vulkan ICD is enabled precisely this way). WSL with no explicit ICD → absent, and
///     that branch is checked BEFORE the manifest one because a standard-directory ICD manifest is not a reliable proxy there.
///     Bare-metal Linux → present iff a Vulkan ICD manifest (<c>*.json</c>) exists under <c>/usr/share/vulkan/icd.d</c> or
///     <c>/etc/vulkan/icd.d</c>. Any IO/permission error while inspecting the filesystem reads as "unknown" and resolves to absent.
/// </remarks>
public sealed class DefaultVulkanDeviceProbe : IVulkanDeviceProbe
{
    /// <summary>Standard Vulkan ICD manifest directories inspected for a <c>*.json</c> driver manifest on bare-metal Linux.</summary>
    private static readonly string[] IcdManifestDirectories = ["/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d"];

    private const string VulkanIcdFilenamesEnvironmentVariable = "VK_ICD_FILENAMES";
    private const string WslDistroNameEnvironmentVariable = "WSL_DISTRO_NAME";

    private readonly Func<bool> _hasExplicitIcdEnvironment;
    private readonly Func<bool> _hasIcdManifest;
    private readonly Func<bool> _isWsl;
    private readonly Lazy<bool> _result;

    /// <summary>Creates a probe over the live host: real environment variables and the standard Vulkan ICD directories.</summary>
    public DefaultVulkanDeviceProbe()
        : this(DetectExplicitIcdEnvironmentFromHost, DetectWslFromHost, DetectIcdManifestFromHost)
    {
    }

    /// <summary>
    ///     Test seam: injects the three host signals (explicit-ICD env, WSL, ICD manifest present) as delegates so the
    ///     decision can be exercised without touching the real filesystem or process environment.
    /// </summary>
    internal DefaultVulkanDeviceProbe(Func<bool> hasExplicitIcdEnvironment, Func<bool> isWsl, Func<bool> hasIcdManifest)
    {
        _hasExplicitIcdEnvironment = hasExplicitIcdEnvironment ?? throw new ArgumentNullException(nameof(hasExplicitIcdEnvironment));
        _isWsl = isWsl ?? throw new ArgumentNullException(nameof(isWsl));
        _hasIcdManifest = hasIcdManifest ?? throw new ArgumentNullException(nameof(hasIcdManifest));
        _result = new Lazy<bool>(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public bool HasEnumerableVulkanDevice()
    {
        return _result.Value;
    }

    private bool Probe()
    {
        try
        {
            // Explicit ICD path: Vulkan has been pointed at a concrete driver manifest — trusted on any Linux (incl. WSL).
            if (_hasExplicitIcdEnvironment())
            {
                return true;
            }

            // WSL without an explicit ICD: the NVIDIA GPU is exposed via CUDA/dxcore, not Vulkan, so no Vulkan device
            // enumerates. Checked before the manifest proxy because standard-directory manifests are unreliable here.
            if (_isWsl())
            {
                return false;
            }

            // Bare-metal Linux: a Vulkan ICD manifest is a reasonable proxy that a device will enumerate.
            return _hasIcdManifest();
        }
        catch (IOException)
        {
            // Unknown → fail-safe to absent: a wrong Vulkan pick hard-fails sd-server, whereas CPU always works.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool DetectExplicitIcdEnvironmentFromHost()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VulkanIcdFilenamesEnvironmentVariable));
    }

    private static bool DetectWslFromHost()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WslDistroNameEnvironmentVariable)))
        {
            return true;
        }

        return FileContainsWslSignature("/proc/sys/kernel/osrelease") || FileContainsWslSignature("/proc/version");
    }

    private static bool FileContainsWslSignature(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // Forced sync: this runs behind the Func<bool> probe seam the constructor binds (DetectWslFromHost), which has
        // no async shape; the source is procfs, which never blocks on a device.
#pragma warning disable MA0045
        var contents = File.ReadAllText(path);
#pragma warning restore MA0045
        return contents.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
               || contents.Contains("WSL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetectIcdManifestFromHost()
    {
        return IcdManifestDirectories
               .Where(Directory.Exists)
               .Any(directory => Directory.EnumerateFiles(directory, "*.json").Any());
    }
}
