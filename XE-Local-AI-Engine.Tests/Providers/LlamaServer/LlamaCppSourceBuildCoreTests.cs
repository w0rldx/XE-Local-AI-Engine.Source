namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LlamaCppSourceBuildCoreTests
{
    [Test]
    [Arguments(LlamaCppSourceBackend.Cpu)]
    [Arguments(LlamaCppSourceBackend.Vulkan)]
    [Arguments(LlamaCppSourceBackend.Cuda)]
    public void Normalize_OfficialBackendWithoutCommit_UsesCanonicalRepository(LlamaCppSourceBackend backend)
    {
        var normalized = LlamaCppSourceBuildRequestValidation.Normalize(new LlamaCppSourceBuildRequest(backend, LlamaCppSourceSelection.Official));

        AssertEx.Equal(LlamaCppSourceBuildRequestValidation.OfficialRepository, normalized.Repository);
        AssertEx.Null(normalized.Commit);
    }

    [Test]
    public void Normalize_CustomRepositoryAndUppercaseCommit_CanonicalizesBoth()
    {
        var normalized = LlamaCppSourceBuildRequestValidation.Normalize(new LlamaCppSourceBuildRequest(
            LlamaCppSourceBackend.Cpu,
            LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork.git",
            "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
            AcknowledgeCustomSourceRisk: true));

        AssertEx.Equal("https://github.com/example/fork", normalized.Repository);
        AssertEx.Equal("abcdefabcdefabcdefabcdefabcdefabcdefabcd", normalized.Commit);
    }

    [Test]
    [Arguments("http://github.com/example/fork")]
    [Arguments("https://user@github.com/example/fork")]
    [Arguments("https://github.com:444/example/fork")]
    [Arguments("https://github.com:443/example/fork")]
    [Arguments("https://github.com/example/fork/extra")]
    [Arguments("https://gitlab.com/example/fork")]
    [Arguments("git@github.com:example/fork.git")]
    public void Normalize_CustomUnsafeRepository_Rejects(string repository)
    {
        Assert.Throws<LlamaRuntimeException>(() => LlamaCppSourceBuildRequestValidation.Normalize(new LlamaCppSourceBuildRequest(
            LlamaCppSourceBackend.Cpu,
            LlamaCppSourceSelection.Custom,
            repository,
            AcknowledgeCustomSourceRisk: true)));
    }

    [Test]
    public void Normalize_CustomWithoutAcknowledgement_Rejects()
    {
        Assert.Throws<LlamaRuntimeException>(() => LlamaCppSourceBuildRequestValidation.Normalize(new LlamaCppSourceBuildRequest(
            LlamaCppSourceBackend.Cpu,
            LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork")));
    }

    [Test]
    [Arguments("abc")]
    [Arguments("gggggggggggggggggggggggggggggggggggggggg")]
    public void Normalize_InvalidCommit_Rejects(string commit)
    {
        Assert.Throws<LlamaRuntimeException>(() => LlamaCppSourceBuildRequestValidation.Normalize(new LlamaCppSourceBuildRequest(
            LlamaCppSourceBackend.Cpu,
            LlamaCppSourceSelection.Official,
            Commit: commit)));
    }

    [Test]
    public void ConfigureArguments_Cpu_DisablesBothGpuBackends()
    {
        var args = LlamaCppSourceBuildService.BuildConfigureArguments("build", "source", GpuVariant.Cpu, null);

        AssertEx.True(args.Contains("-DGGML_CUDA=OFF"));
        AssertEx.True(args.Contains("-DGGML_VULKAN=OFF"));
        AssertEx.False(args.Any(static value => value.StartsWith("-DCMAKE_CUDA_ARCHITECTURES=", StringComparison.Ordinal)));
    }

    [Test]
    public void CloneCommands_OfficialPinned_UsesPinnedTagWithoutSubmodules()
    {
        var descriptor = new LlamaCppSourceBuildDescriptor(GpuVariant.Cpu, LlamaCppSourceSelection.Official,
            LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppSourceRevisionMode.EnginePinned, null,
            LlamaCppReleasePins.PinnedSourceCommitSha);
        var command = LlamaCppSourceBuildService.BuildCloneCommands(descriptor, "/clone").Single();
        AssertEx.Contains(command, "--no-recurse-submodules");
        AssertEx.Contains(command, LlamaCppReleasePins.PinnedTag);
    }

    [Test]
    public void CloneCommands_CustomDefault_ClonesDefaultHeadWithoutInjectedRef()
    {
        var descriptor = new LlamaCppSourceBuildDescriptor(GpuVariant.Cpu, LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork", LlamaCppSourceRevisionMode.DefaultBranch, null, null);
        var command = LlamaCppSourceBuildService.BuildCloneCommands(descriptor, "/clone").Single();
        AssertEx.Contains(command, "https://github.com/example/fork");
        AssertEx.False(command.Contains("--branch"));
    }

    [Test]
    public void CloneCommands_ExplicitCommit_FetchesShaAndChecksOutDetached()
    {
        var sha = new string('a', 40);
        var descriptor = new LlamaCppSourceBuildDescriptor(GpuVariant.Cpu, LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork", LlamaCppSourceRevisionMode.ExplicitCommit, sha, null);
        var commands = LlamaCppSourceBuildService.BuildCloneCommands(descriptor, "/clone");
        AssertEx.True(commands.Any(command => command.Contains("fetch") && command.Contains(sha)));
        AssertEx.True(commands.Any(command => command.Contains("checkout") && command.Contains("--detach") && command.Contains(sha)));
    }

    [Test]
    public void ConfigureArguments_Vulkan_EnablesOnlyVulkan()
    {
        var args = LlamaCppSourceBuildService.BuildConfigureArguments("build", "source", GpuVariant.Vulkan, null);

        AssertEx.True(args.Contains("-DGGML_CUDA=OFF"));
        AssertEx.True(args.Contains("-DGGML_VULKAN=ON"));
    }

    [Test]
    public void ConfigureArguments_Cuda_EnablesOnlyCudaAndCarriesArchitectures()
    {
        var args = LlamaCppSourceBuildService.BuildConfigureArguments("build", "source", GpuVariant.Cuda, "75;89");

        AssertEx.True(args.Contains("-DGGML_CUDA=ON"));
        AssertEx.True(args.Contains("-DGGML_VULKAN=OFF"));
        AssertEx.True(args.Contains("-DCMAKE_CUDA_ARCHITECTURES=75;89"));
    }

    [Test]
    public void ParseCudaArchitectures_DeduplicatesAndSortsStrictCapabilities()
    {
        AssertEx.Equal("75;86;89", LlamaCppSourceBuildService.ParseCudaArchitectures("8.9\n7.5\n8.6\n8.9\n"));
    }

    [Test]
    [Arguments("native")]
    [Arguments("4.9")]
    [Arguments("8.10")]
    [Arguments("8.9 extra")]
    public void ParseCudaArchitectures_InvalidOrOutOfBounds_FallsBack(string output)
    {
        AssertEx.Equal("75;86;89", LlamaCppSourceBuildService.ParseCudaArchitectures(output));
    }

    [Test]
    public async Task Selector_ActiveCpuSource_ReturnsCpuWithoutVendorOverride()
    {
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cpu);
        var selector = new GpuVariantSelector(new FixedVendorProbe(DetectedGpuVendor.Nvidia), isWindows: false, managedCudaSignal: signal);

        AssertEx.Equal(GpuVariant.Cpu, await selector.SelectVariantAsync(CancellationToken.None));
    }

    [Test]
    public void ActiveSignal_EveryMutationAdvancesVersionAndCpuIsNotAbsent()
    {
        var signal = new CudaManagedBuildSignal();
        var initial = signal.Version;
        signal.SetActive(GpuVariant.Cpu);
        AssertEx.Equal(GpuVariant.Cpu, signal.ActiveVariant);
        AssertEx.True(signal.Version > initial);
        var activeVersion = signal.Version;
        signal.Clear();
        AssertEx.Null(signal.ActiveVariant);
        AssertEx.True(signal.Version > activeVersion);
    }

    private sealed class FixedVendorProbe(DetectedGpuVendor vendor) : IGpuVendorProbe
    {
        public Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct) => Task.FromResult(vendor);
    }
}
