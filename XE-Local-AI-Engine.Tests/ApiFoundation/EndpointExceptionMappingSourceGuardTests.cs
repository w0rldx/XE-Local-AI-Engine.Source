namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class EndpointExceptionMappingSourceGuardTests
{
    private static readonly IReadOnlyDictionary<string, int> TrainingCatchAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // The rejection families (EvaluationRejected / TrainingExportRejected / TrainingRunRejected) are no longer
        // here: they are in DomainValidationExceptionHandler's switch, so their endpoints catch nothing at all.
        ["BaseArtifacts/V1/CreateBaseArtifactEndpoint.cs"] = 1,
        ["Runtime/V1/RemoveTrainingRuntimeEndpoint.cs"] = 1,
        ["Runtime/V1/StartTrainingRuntimeInstallEndpoint.cs"] = 1,
        ["V1/TrainingEndpointSupport.cs"] = 1
    };

    private static readonly IReadOnlyDictionary<string, int> BenchmarkCatchAllowlist = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["V1/BenchmarkExportPairwise.cs"] = 1,
        ["V1/Mappers/BenchmarkEndpointMapper.cs"] = 1,
        // Both catches came from BenchmarkRunEndpoints.cs, which S7f-1 split one endpoint per file.
        ["V1/StartBenchmarkRunEndpoint.cs"] = 2
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

    [Test]
    public void GlobalHandlers_RegisterWorkSessionNotFoundBeforeTheDefaultHandler()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(root, "XE-Local-AI-Engine.Client", "ConfigureServices.cs"));
        var workSessionHandler = composition.IndexOf(".AddExceptionHandler<WorkSessionNotFoundExceptionHandler>()", StringComparison.Ordinal);
        var defaultHandler = composition.IndexOf(".AddExceptionHandler<DefaultExceptionHandler>()", StringComparison.Ordinal);

        AssertEx.True(workSessionHandler >= 0, "The typed WorkSession not-found handler must be registered in the global exception chain.");
        AssertEx.True(defaultHandler >= 0, "The default exception handler registration must remain present.");
        AssertEx.True(workSessionHandler < defaultHandler, "The typed WorkSession not-found handler must run before the default 500 handler.");
    }

    [Test]
    public void WorkSessionEndpoints_DoNotTranslateRawKeyNotFoundExceptionsToNotFound()
    {
        var root = FindRepositoryRoot();
        var workSessionEndpoints = Path.Combine(root, "XE-Local-AI-Engine.Client", "Endpoints", "WorkSessions");
        var offenders = Directory.EnumerateFiles(workSessionEndpoints, "*.cs", SearchOption.AllDirectories)
                                 .Where(path => File.ReadAllText(path).Contains("catch (KeyNotFoundException)", StringComparison.Ordinal))
                                 .Select(path => Path.GetRelativePath(workSessionEndpoints, path).Replace('\\', '/'))
                                 .OrderBy(static path => path, StringComparer.Ordinal)
                                 .ToArray();

        AssertEx.Empty(offenders,
            "WorkSession endpoint-local raw KeyNotFoundException catches can turn unrelated defects into 404. Throw the typed persistence family and let its global handler map it.");
    }

    [Test]
    public void GlobalHandlers_RegisterDevWorkflowNotFoundBeforeTheDefaultHandler()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(root, "XE-Local-AI-Engine.Client", "ConfigureServices.cs"));
        var devWorkflowHandler = composition.IndexOf(".AddExceptionHandler<DevWorkflowNotFoundExceptionHandler>()", StringComparison.Ordinal);
        var defaultHandler = composition.IndexOf(".AddExceptionHandler<DefaultExceptionHandler>()", StringComparison.Ordinal);

        AssertEx.True(devWorkflowHandler >= 0, "The typed DevWorkflow not-found handler must be registered in the global exception chain.");
        AssertEx.True(defaultHandler >= 0, "The default exception handler registration must remain present.");
        AssertEx.True(devWorkflowHandler < defaultHandler, "The typed DevWorkflow not-found handler must run before the default 500 handler.");
    }

    /// <summary>
    ///     The sweep above is hardcoded per family, so a new endpoint folder is not covered by it — "still green" after
    ///     adding one would be true trivially. This is that folder's own sweep.
    /// </summary>
    [Test]
    public void DevWorkflowEndpoints_CatchNothingAndTranslateNoRawKeyNotFoundExceptions()
    {
        var root = FindRepositoryRoot();
        var endpoints = Path.Combine(root, "XE-Local-AI-Engine.Client", "Endpoints", "DevelopmentWorkflows");
        AssertEx.True(Directory.Exists(endpoints), "The development-workflow endpoint folder must exist for this guard to mean anything.");

        var offenders = Directory.EnumerateFiles(endpoints, "*.cs", SearchOption.AllDirectories)
                                 .Where(path => CountCatches(File.ReadAllText(path)) > 0)
                                 .Select(path => Path.GetRelativePath(endpoints, path).Replace('\\', '/'))
                                 .OrderBy(static path => path, StringComparer.Ordinal)
                                 .ToArray();

        AssertEx.Empty(offenders,
            "Development-workflow endpoints must throw the typed persistence and runtime families and let the global handlers map them. "
            + "An endpoint-local catch turns unrelated defects into 404s and 400s, and duplicates a mapping that already exists in one place.");
    }

    [Test]
    public void GlobalHandlers_RegisterTheDevelopmentHandlersBeforeTheDefaultHandler()
    {
        var root = FindRepositoryRoot();
        var composition = File.ReadAllText(Path.Combine(root, "XE-Local-AI-Engine.Client", "ConfigureServices.cs"));
        var defaultHandler = composition.IndexOf(".AddExceptionHandler<DefaultExceptionHandler>()", StringComparison.Ordinal);

        AssertEx.True(defaultHandler >= 0, "The default exception handler registration must remain present.");

        foreach (var handler in new[]
                 {
                     "DevelopmentNotFoundExceptionHandler",
                     "DevelopmentConflictExceptionHandler"
                 })
        {
            var index = composition.IndexOf($".AddExceptionHandler<{handler}>()", StringComparison.Ordinal);
            AssertEx.True(index >= 0, $"{handler} must be registered in the global exception chain.");
            AssertEx.True(index < defaultHandler, $"{handler} must run before the default 500 handler.");
        }
    }

    /// <summary>
    ///     The Development folder's own sweep, mirroring the development-workflow one above. A raw
    ///     <c>KeyNotFoundException</c> catch here turned any unrelated dictionary miss in the pipeline into a 404; the
    ///     store now throws <c>DevelopmentNotFoundException</c> and the global handler answers it.
    /// </summary>
    [Test]
    public void DevelopmentEndpoints_TranslateNoRawKeyNotFoundExceptions()
    {
        var root = FindRepositoryRoot();
        var endpoints = Path.Combine(root, "XE-Local-AI-Engine.Client", "Endpoints", "Development");
        AssertEx.True(Directory.Exists(endpoints), "The Development endpoint folder must exist for this guard to mean anything.");

        var offenders = Directory.EnumerateFiles(endpoints, "*.cs", SearchOption.AllDirectories)
                                 .Where(path => File.ReadAllText(path).Contains("catch (KeyNotFoundException", StringComparison.Ordinal))
                                 .Select(path => Path.GetRelativePath(endpoints, path).Replace('\\', '/'))
                                 .OrderBy(static path => path, StringComparer.Ordinal)
                                 .ToArray();

        AssertEx.Empty(offenders,
            "Development endpoints must let DevelopmentNotFoundException reach its global handler instead of catching the bare CLR type.");
    }

    /// <summary>
    ///     The Development catches that remain, each because no single global status is correct for what it maps:
    ///     the workspace-security family (409 on patch/next-action, 400 on register/create/reconnect) and the
    ///     framework types (an <c>ArgumentException</c> filter that would otherwise claim every
    ///     <c>ArgumentNullException</c> in the node, plus the file-system ones). Adding a row needs the same argument.
    /// </summary>
    [Test]
    public void DevelopmentEndpointCatches_AreLimitedToTheTypesWithNoSingleGlobalStatus()
    {
        var root = FindRepositoryRoot();
        AssertCatchAllowlist(Path.Combine(root, "XE-Local-AI-Engine.Client", "Endpoints", "Development"),
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                // Ten catches, unchanged in total: S7f-1 split the five plural files this list used to name
                // one endpoint per file, so each count moved to the endpoint that already carried it.
                ["V1/ApplyDevelopmentPatchEndpoint.cs"] = 1, // was PatchDevelopmentEndpoints.cs = 2
                ["V1/PreviewDevelopmentPatchEndpoint.cs"] = 1, //   "
                ["V1/CreateDevelopmentProjectEndpoint.cs"] = 1, // was ProjectDevelopmentEndpoints.cs = 1
                ["V1/DetectDevelopmentRepositoryProfileEndpoint.cs"] = 1, // was RepositoryDevelopmentEndpoints.cs = 4
                ["V1/ReconnectDevelopmentRepositoryEndpoint.cs"] = 2, //   "
                ["V1/RegisterDevelopmentRepositoryEndpoint.cs"] = 1, //   "
                ["V1/StartDevelopmentNextActionEndpoint.cs"] = 1, // was TaskDevelopmentEndpoints.cs = 1
                ["V1/CreateDevelopmentRepositoryFromTemplateEndpoint.cs"] = 1, // was TemplateDevelopmentEndpoints.cs = 2
                ["V1/RegisterDevelopmentTemplateEndpoint.cs"] = 1 //   "
            });
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
