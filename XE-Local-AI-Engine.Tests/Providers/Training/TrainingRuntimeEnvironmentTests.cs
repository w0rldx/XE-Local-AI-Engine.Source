namespace XE_Local_AI_Engine.Tests.Providers.Training;

using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The CUDA driver's kernel cache defaults to <c>~/.nv/ComputeCache</c>, and a run's HOME lives under its work
///     directory: unless the cache is pointed at the machine-global cache root, every run starts it cold.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TrainingRuntimeEnvironmentTests
{
    [Test]
    public void BuildTrainEnvironment_PutsTheCudaKernelCacheUnderTheCacheRootAtItsMaximumSize()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), "cache-root");
        var workDirectory = Path.Combine(Path.GetTempPath(), "run", "work");

        var environment = TrainingRuntimeEnvironment.BuildTrainEnvironment(cacheRoot, workDirectory);

        var cudaCache = environment["CUDA_CACHE_PATH"];
        AssertEx.True(cudaCache.StartsWith(cacheRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"The kernel cache must outlive the run: {cudaCache}");
        AssertEx.False(cudaCache.StartsWith(workDirectory, StringComparison.Ordinal),
            "A cache under the work directory dies with the run's work/ sweep.");
        AssertEx.Equal("4294967296", environment["CUDA_CACHE_MAXSIZE"], "NVIDIA documents 4 GiB as the maximum.");
    }
}
