namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class EndpointExceptionMappingSourceGuardTests
{
    private static readonly IReadOnlyDictionary<string, int> TrainingCatchAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["BaseArtifacts/V1/CreateBaseArtifactEndpoint.cs"] = 1,
        ["Comparisons/V1/TrainingComparisonEndpoints.cs"] = 2,
        ["Evaluations/V1/TrainingEvaluationEndpoints.cs"] = 2,
        ["Exports/V1/TrainingExportEndpoints.cs"] = 5,
        ["Runs/V1/TrainingRunEndpoints.cs"] = 2,
        ["Runtime/V1/RemoveTrainingRuntimeEndpoint.cs"] = 1,
        ["Runtime/V1/StartTrainingRuntimeInstallEndpoint.cs"] = 1,
        ["V1/TrainingEndpointSupport.cs"] = 1
    };

    private static readonly IReadOnlyDictionary<string, int> BenchmarkCatchAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["V1/BenchmarkExportEndpoints.cs"] = 3,
        ["V1/BenchmarkRunEndpoints.cs"] = 5,
        ["V1/Mappers/BenchmarkEndpointMapper.cs"] = 1
    };

    [Test]
    public void EndpointCatches_AreLimitedToTheReviewedContextualBatchAndRecoverySites()
    {
        var root = FindRepositoryRoot();
        AssertCatchAllowlist(Path.Combine(root, "XE-Local-AI-Engine.Client", "Endpoints", "Training"), TrainingCatchAllowlist);
        AssertCatchAllowlist(Path.Combine(root, "XE-Local-AI-Engine.Client", "Endpoints", "Benchmarks"), BenchmarkCatchAllowlist);
    }

    [Test]
    public void GlobalHandlers_UseTypedFamiliesWithoutRouteOrKeyNotFoundHeuristics()
    {
        var root = FindRepositoryRoot();
        var exceptionHandling = Path.Combine(root, "XE-Local-AI-Engine.Client", "ExceptionHandling");
        var training = File.ReadAllText(Path.Combine(exceptionHandling, "TrainingExceptionHandler.cs"));
        var benchmark = File.ReadAllText(Path.Combine(exceptionHandling, "BenchmarkExceptionHandler.cs"));

        AssertEx.Contains(training, "exception is not TrainingStoreException", StringComparison.Ordinal);
        AssertEx.Contains(benchmark, "BenchmarkEndpointSupport.IsHandled(exception)", StringComparison.Ordinal);
        AssertEx.False(training.Contains("Request.Path", StringComparison.Ordinal));
        AssertEx.False(benchmark.Contains("Request.Path", StringComparison.Ordinal));
        AssertEx.False(training.Contains("exception is KeyNotFoundException", StringComparison.Ordinal));
        AssertEx.False(benchmark.Contains("exception is KeyNotFoundException", StringComparison.Ordinal));
    }

    private static void AssertCatchAllowlist(string familyRoot, IReadOnlyDictionary<string, int> expected)
    {
        var actual = Directory.EnumerateFiles(familyRoot, "*.cs", SearchOption.AllDirectories)
                              .Select(path => new
                              {
                                  RelativePath = Path.GetRelativePath(familyRoot, path).Replace('\\', '/'),
                                  CatchCount = CountCatches(File.ReadAllText(path))
                              })
                              .Where(static item => item.CatchCount > 0)
                              .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
                              .ToArray();
        var expectedLines = expected.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                                    .Select(static pair => $"{pair.Key}:{pair.Value}");
        var actualLines = actual.Select(static item => $"{item.RelativePath}:{item.CatchCount}");

        AssertEx.Equal(string.Join(Environment.NewLine, expectedLines),
            string.Join(Environment.NewLine, actualLines),
            "Endpoint-local catches require an explicit contextual, batch, recovery, or compensation review before entering this allowlist.");
    }

    private static int CountCatches(string source) =>
        source.Split("catch (", StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        foreach (var seed in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(seed); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "XE-Local-AI-Engine.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("The repository root containing XE-Local-AI-Engine.slnx was not found.");
    }
}
