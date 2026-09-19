namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

[NotInParallel]
[Category(TestCategories.Unit)]
public sealed class LlamaCppSourceBuildPrerequisiteTests
{
    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Probe_Cpu_DoesNotRequireCudaOrVulkanTools()
    {
        using var temp = new TempDirectory();
        WriteCommonTools(temp.Path);
        using var path = new PathScope(temp.Path);
        var probe = new LlamaCppSourceBuildPrerequisiteProbe(new VendorProbe(), temp.Path, requiredFreeDiskBytes: 0);

        var report = await probe.ProbeAsync(LlamaCppSourceBackend.Cpu, CancellationToken.None);

        AssertEx.True(report.CanBuild);
        AssertEx.False(report.Items.Any(static item => item.Key is "nvcc" or "nvidia-gpu" or "glslc" or "vulkaninfo"));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Probe_Vulkan_RequiresGlslcAndVulkanInfoButNotNvcc()
    {
        using var temp = new TempDirectory();
        WriteCommonTools(temp.Path);
        using var path = new PathScope(temp.Path);
        var probe = new LlamaCppSourceBuildPrerequisiteProbe(new VendorProbe(), temp.Path, requiredFreeDiskBytes: 0);

        var report = await probe.ProbeAsync(LlamaCppSourceBackend.Vulkan, CancellationToken.None);

        AssertEx.False(report.CanBuild);
        AssertEx.True(report.Items.Any(static item => item.Key == "glslc" && !item.Satisfied));
        AssertEx.True(report.Items.Any(static item => item.Key == "vulkaninfo" && !item.Satisfied));
        AssertEx.False(report.Items.Any(static item => item.Key == "nvcc"));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Probe_Cuda_RequiresNvidiaCompilerAndDriverProbe()
    {
        using var temp = new TempDirectory();
        WriteCommonTools(temp.Path);
        using var path = new PathScope(temp.Path);

        // An empty CUDA root, not an unset one: the probe now falls back to /usr/local/cuda/bin when neither
        // CUDA_HOME nor CUDA_PATH is set, and a gate machine with a real CUDA install would otherwise satisfy the
        // row this test is about. BOTH variables are pinned — the locator consults CUDA_PATH as well, so a box that
        // exports it (NVIDIA's own installers do) would find a real compiler through the one left unpinned. Pinning
        // both is how "no nvcc anywhere" stays a property of the test, not of the box.
        using var cudaHome = new EnvironmentScope("CUDA_HOME", temp.Path);
        using var cudaPath = new EnvironmentScope("CUDA_PATH", value: null);
        var probe = new LlamaCppSourceBuildPrerequisiteProbe(new VendorProbe(), temp.Path, requiredFreeDiskBytes: 0);

        var report = await probe.ProbeAsync(LlamaCppSourceBackend.Cuda, CancellationToken.None);

        AssertEx.False(report.CanBuild);
        AssertEx.True(report.Items.Any(static item => item.Key == "nvcc" && !item.Satisfied));
        AssertEx.True(report.Items.Any(static item => item.Key == "nvidia-smi" && !item.Satisfied));
    }

    /// <summary>
    ///     The reported defect: <c>nvcc</c> installed where NVIDIA's Linux installer puts it, with that directory not on
    ///     the host PATH, was reported "Missing" — while the build it gates would have found the same toolkit through
    ///     CMake. The row must be satisfied from the conventional root, and its key must stay the bare tool name so no
    ///     absolute path reaches the operator-facing checklist.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Probe_Cuda_FindsNvccUnderTheConfiguredCudaRootWhenItIsNotOnPath()
    {
        using var temp = new TempDirectory();
        WriteCommonTools(temp.Path);
        using var path = new PathScope(temp.Path);

        var cudaBin = Path.Combine(temp.Path, "cuda", "bin");
        Directory.CreateDirectory(cudaBin);
        WriteTool(cudaBin, "nvcc");

        // Both pinned for the reason the sibling test above documents: the answer must come from this temp root,
        // never from whatever the box exports or has installed under /usr/local/cuda.
        using var cudaHome = new EnvironmentScope("CUDA_HOME", Path.Combine(temp.Path, "cuda"));
        using var cudaPath = new EnvironmentScope("CUDA_PATH", value: null);

        var probe = new LlamaCppSourceBuildPrerequisiteProbe(new VendorProbe(), temp.Path, requiredFreeDiskBytes: 0);

        var report = await probe.ProbeAsync(LlamaCppSourceBackend.Cuda, CancellationToken.None);

        var nvcc = report.Items.Single(static item => item.Key == "nvcc");
        AssertEx.True(nvcc.Satisfied, "nvcc under the configured CUDA root must satisfy the row even when PATH has no nvcc.");
        AssertEx.False(nvcc.Detail.Contains(temp.Path, StringComparison.Ordinal), "The checklist detail must never surface an absolute host path.");
    }

    /// <summary>
    ///     The free-disk row must describe the mount the build cache actually sits on. It measured the path's ROOT
    ///     instead, which on Linux is <c>/</c> for every absolute path there is, so a cache on a redirected data
    ///     volume was gated on the root filesystem's free space.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Probe_FreeDisk_MeasuresTheMountHoldingTheBuildCacheRootRatherThanTheRootFilesystem()
    {
        using var scratch = SeparateMountScratch.CreateOrSkip("xe-llama-source-prereq-disk");

        // An empty PATH: the toolchain rows are not the subject and this keeps the probe from spawning six real tools.
        using var path = new PathScope(scratch.Path);
        var probe = new LlamaCppSourceBuildPrerequisiteProbe(new VendorProbe(), scratch.Path, scratch.ThresholdBetweenBytes);

        var report = await probe.ProbeAsync(LlamaCppSourceBackend.Cpu, CancellationToken.None);

        var freeDisk = report.Items.Single(static item => item.Key == "free-disk");
        AssertEx.Equal(scratch.ExpectedSatisfied, freeDisk.Satisfied, scratch.WrongMountMessage);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteCommonTools(string directory)
    {
        foreach (var tool in new[]
                 {
                     "cmake",
                     "gcc",
                     "g++",
                     "make",
                     "git"
                 })
        {
            WriteTool(directory, tool);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteTool(string directory, string tool)
    {
        var path = Path.Combine(directory, tool);
        File.WriteAllText(path, $"#!/bin/sh\necho '{tool} 1.0'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() =>
            Environment.SetEnvironmentVariable(_name, _original);
    }

    private sealed class VendorProbe : IGpuVendorProbe
    {
        public Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct) =>
            Task.FromResult(DetectedGpuVendor.Nvidia);
    }

    private sealed class PathScope : IDisposable
    {
        private readonly string _original = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        public PathScope(string path) =>
            Environment.SetEnvironmentVariable("PATH", path);

        public void Dispose() =>
            Environment.SetEnvironmentVariable("PATH", _original);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-source-prereq-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception)
            {
                /* Best-effort test cleanup. */
            }
        }
    }
}
