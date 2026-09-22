namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Default <see cref="ICudaDeviceProbe" />: decides whether a CUDA device will enumerate from one cheap, host-local
///     signal, the CUDA driver library, with no native load and no child process.
/// </summary>
/// <remarks>
///     Windows is the only OS with a prebuilt CUDA <c>sd-server</c> and so the only one inspected: present iff <c>nvcuda.dll</c> exists
///     in the system directory, where the NVIDIA display driver installs the CUDA driver API. Elsewhere, and on any IO/permission
///     error, the verdict is present, because a false "absent" drops a healthy box to Vulkan. <strong>Ceiling:</strong> it catches a
///     missing driver, not one too old for the binary's CUDA runtime — that still reaches the supervisor, which reports the child's
///     exit code and stderr tail. The verdict is cached for the process lifetime via a thread-safe <see cref="Lazy{T}" />.
/// </remarks>
public sealed class DefaultCudaDeviceProbe : ICudaDeviceProbe
{
    private const string CudaDriverLibrary = "nvcuda.dll";

    private readonly Func<bool> _hasCudaDriverLibrary;
    private readonly bool _isWindows;
    private readonly Lazy<bool> _result;

    /// <summary>Creates a probe over the live host: the real OS and the real system directory.</summary>
    public DefaultCudaDeviceProbe()
        : this(OperatingSystem.IsWindows(), DetectCudaDriverLibraryFromHost)
    {
    }

    /// <summary>Test seam: injects the OS and the driver-library signal so the decision runs without touching the real filesystem.</summary>
    internal DefaultCudaDeviceProbe(bool isWindows, Func<bool> hasCudaDriverLibrary)
    {
        _isWindows = isWindows;
        _hasCudaDriverLibrary = hasCudaDriverLibrary ?? throw new ArgumentNullException(nameof(hasCudaDriverLibrary));
        _result = new Lazy<bool>(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public bool HasEnumerableCudaDevice()
    {
        return _result.Value;
    }

    private bool Probe()
    {
        if (!_isWindows)
        {
            return true;
        }

        try
        {
            return _hasCudaDriverLibrary();
        }
        catch (IOException)
        {
            // Unknown → present: a false "absent" would strand a healthy NVIDIA box on Vulkan.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool DetectCudaDriverLibraryFromHost()
    {
        var systemDirectory = Environment.SystemDirectory;
        return !string.IsNullOrWhiteSpace(systemDirectory)
               && File.Exists(Path.Combine(systemDirectory, CudaDriverLibrary));
    }
}
