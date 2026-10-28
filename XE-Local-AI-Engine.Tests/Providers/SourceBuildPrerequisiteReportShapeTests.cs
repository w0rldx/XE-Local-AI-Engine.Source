namespace XE_Local_AI_Engine.Tests.Providers;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The rendered source-build checklist of both native runtimes: the SPA keys on each row and shows the rows in the
///     order they arrive, so the key order and every detail text that does not come from a tool's own banner are pinned.
/// </summary>
/// <remarks>
///     The probes spawn the host's real tools, so a satisfied tool row carries the tool's first version line and only
///     its presence is asserted; a missing tool must name itself by its display name.
/// </remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
public sealed class SourceBuildPrerequisiteReportShapeTests
{
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        ["nvcc"] = "NVIDIA CUDA compiler (nvcc)",
        ["nvidia-smi"] = "NVIDIA driver probe",
        ["glslc"] = "Vulkan shader compiler (glslc)",
        ["vulkaninfo"] = "Vulkan runtime probe",
        ["cmake"] = "CMake",
        ["gcc"] = "C compiler (gcc)",
        ["g++"] = "C++ compiler (g++)",
        ["git"] = "git",
        ["readelf"] = "readelf (binutils)"
    };

    [Test]
    [Arguments(WhisperBackend.Cpu, 0L)]
    [Arguments(WhisperBackend.Cuda, long.MaxValue)]
    public async Task Whisper_RendersTheChecklistInItsFixedOrder(WhisperBackend backend, long requiredFreeDiskBytes)
    {
        using var temp = new TempDirectory();
        var probe = new WhisperCppSourceBuildPrerequisiteProbe(temp.Path, requiredFreeDiskBytes);

        var report = await probe.ProbeAsync(backend, CancellationToken.None);

        string[] backendKeys = backend == WhisperBackend.Cuda ? ["nvcc", "nvidia-smi"] : [];
        AssertChecklist([.. report.Items.Select(static item => Render(item.Key, item.Satisfied, item.Detail))],
            report.CanBuild,
            [
                "os-is-linux", .. backendKeys, "cmake", "gcc", "g++", "make-or-ninja", "git", "readelf", "free-disk"
            ],
            requiredFreeDiskBytes);
    }

    [Test]
    [Arguments(SdGpuBackend.Cpu, 0L)]
    [Arguments(SdGpuBackend.Cuda, long.MaxValue)]
    [Arguments(SdGpuBackend.Vulkan, 0L)]
    public async Task StableDiffusion_RendersTheChecklistInItsFixedOrder(SdGpuBackend backend, long requiredFreeDiskBytes)
    {
        using var temp = new TempDirectory();
        var probe = new StableDiffusionCppSourceBuildPrerequisiteProbe(temp.Path, requiredFreeDiskBytes);

        var report = await probe.ProbeAsync(backend, CancellationToken.None);

        string[] backendKeys = backend switch
        {
            SdGpuBackend.Cuda => ["nvcc", "nvidia-smi"],
            SdGpuBackend.Vulkan => ["glslc", "vulkaninfo"],
            _ => []
        };
        AssertChecklist([.. report.Items.Select(static item => Render(item.Key, item.Satisfied, item.Detail))],
            report.CanBuild,
            ["os-is-linux", .. backendKeys, "cmake", "gcc", "g++", "make-or-ninja", "git", "free-disk"],
            requiredFreeDiskBytes);
    }

    private static string Render(string key, bool satisfied, string detail)
    {
        return $"{key}|{(satisfied ? "ok" : "missing")}|{detail}";
    }

    private static void AssertChecklist(IReadOnlyList<string> rendered, bool canBuild, IReadOnlyList<string> expectedKeys, long requiredFreeDiskBytes)
    {
        AssertEx.Equal(string.Join(",", expectedKeys),
            string.Join(",", rendered.Select(static row => row[..row.IndexOf('|', StringComparison.Ordinal)])),
            "The checklist rows must keep their keys and order.");
        AssertEx.Equal(rendered.All(static row => row.Contains("|ok|", StringComparison.Ordinal)), canBuild, "CanBuild is true exactly when every row is satisfied.");

        foreach (var row in rendered)
        {
            var key = row[..row.IndexOf('|', StringComparison.Ordinal)];
            var satisfied = row.Contains("|ok|", StringComparison.Ordinal);
            var detail = row[(row.IndexOf('|', key.Length + 1) + 1)..];
            switch (key)
            {
                case "os-is-linux":
                    AssertEx.Equal("os-is-linux|ok|Linux host detected.", row);
                    break;
                case "free-disk":
                    AssertEx.Equal(requiredFreeDiskBytes == 0
                            ? "free-disk|ok|Sufficient free disk space detected."
                            : "free-disk|missing|At least 15 GiB of free disk space is required.",
                        row);
                    break;
                case "make-or-ninja" when !satisfied:
                    AssertEx.Equal("Neither Ninja nor Make is available.", detail);
                    break;
                case "make-or-ninja":
                    AssertEx.NotEmpty(detail);
                    break;
                case var _ when !satisfied:
                    AssertEx.True(detail == $"{DisplayNames[key]} is not available." || detail == $"{DisplayNames[key]} probe timed out.",
                        $"A missing tool must name itself; got '{row}'.");
                    break;
                default:
                    AssertEx.NotEmpty(detail, $"A satisfied tool row carries its version line; got '{row}'.");
                    break;
            }
        }
    }
}
