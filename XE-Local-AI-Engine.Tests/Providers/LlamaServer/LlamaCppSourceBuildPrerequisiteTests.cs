namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

[NotInParallel]
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
        var probe = new LlamaCppSourceBuildPrerequisiteProbe(new VendorProbe(), temp.Path, requiredFreeDiskBytes: 0);

        var report = await probe.ProbeAsync(LlamaCppSourceBackend.Cuda, CancellationToken.None);

        AssertEx.False(report.CanBuild);
        AssertEx.True(report.Items.Any(static item => item.Key == "nvcc" && !item.Satisfied));
        AssertEx.True(report.Items.Any(static item => item.Key == "nvidia-smi" && !item.Satisfied));
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
            var path = Path.Combine(directory, tool);
            File.WriteAllText(path, $"#!/bin/sh\necho '{tool} 1.0'\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
