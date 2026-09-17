namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using FastEndpoints;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins WHAT a FastEndpoints endpoint in the host is allowed to take as a constructor dependency.
///     <para>
///         An endpoint is the HTTP edge of the host. It may compose the application layer, the shared contracts and
///         framework services; it may not reach past them into a store, a concrete provider's implementation surface,
///         the EF Core API, the Docker SDK, or a raw process / socket / file handle. Those are decisions the
///         application layer owns, and an endpoint that makes them itself is a second, undocumented copy of a service.
///     </para>
///     <para>
///         Reflection over the compiled host rather than a source scan or an ArchUnitNET/NetArchTest rule: the rule is
///         per-constructor-PARAMETER with a named exception list, and neither rule engine can express "type X may
///         depend on Y except for these named pairs". A bespoke loop over <see cref="ConstructorInfo" /> is the
///         smallest mechanism that can, and it reads the same IL the host actually runs.
///     </para>
///     <para>
///         Generic arguments, array element types and <c>Nullable&lt;T&gt;</c> underlying types are inspected
///         recursively, so <c>IOptions&lt;CodexOptions&gt;</c> counts as a dependency on <c>CodexOptions</c> even
///         though <c>IOptions&lt;T&gt;</c> itself is an allowed framework type. Classifying only the outer type
///         silently exempts any forbidden type wrapped in an allowed generic.
///     </para>
///     <para>
///         Ratchet (P1): <see cref="AllowedViolations" /> freezes the violations that exist today so no NEW one can
///         land. Slice S6 migrates those sites behind application-layer services and deletes both the list and
///         <see cref="EveryAllowlistedPair_StillViolatesToday" />.
///     </para>
/// </summary>
public sealed class EndpointDependencyTests
{
    private const string ClientNamespace = "XE_Local_AI_Engine.Client";
    private const string PersistenceNamespace = "XE_Local_AI_Engine.Client.Persistence";
    private const string ProvidersNamespace = "XE_Local_AI_Engine.Providers";
    private const string AbstractionsNamespace = "XE_Local_AI_Engine.Providers.Abstractions";

    private static readonly Assembly ClientAssembly = typeof(WorkerHealthCheck).Assembly;

    /// <summary>
    ///     Namespace roots an endpoint constructor may take a parameter from. Anything outside this set is a
    ///     violation, which is what makes the rule a fence rather than a blacklist: a new third-party SDK injected
    ///     straight into an endpoint fails without anyone having remembered to ban it.
    /// </summary>
    private static readonly string[] AllowedNamespaces =
    [
        "XE_Local_AI_Engine.Client.Application",
        "XE_Local_AI_Engine.AI.Contracts",
        AbstractionsNamespace,
        ClientNamespace,
        "Microsoft",
        "System",
        "FastEndpoints"
    ];

    /// <summary>
    ///     The roots that are forbidden even though an allowed root is a prefix of them.
    ///     <c>XE_Local_AI_Engine.Client.Persistence</c> sits under the host's own root; <c>Microsoft.EntityFrameworkCore</c>
    ///     under <c>Microsoft</c>. Order matters: forbidden wins over allowed.
    /// </summary>
    private static readonly string[] ForbiddenNamespaces =
    [
        PersistenceNamespace,
        "Microsoft.EntityFrameworkCore",
        "Docker.DotNet"
    ];

    /// <summary>
    ///     Types that are forbidden by name rather than by namespace — the rest of <c>System.Diagnostics</c>,
    ///     <c>System.Net.Http</c> and <c>System.IO</c> is ordinary framework surface.
    /// </summary>
    private static readonly string[] ForbiddenTypes =
    [
        "System.Diagnostics.Process",
        "System.IO.File",
        "System.Net.Http.HttpClient"
    ];

    /// <summary>
    ///     P1 ratchet list, keyed <c>"{EndpointTypeFullName}|{ParameterTypeFullName}"</c> — fully qualified on both
    ///     sides, because a short name can collide across endpoint areas, and because a per-endpoint key would let an
    ///     already-listed endpoint acquire a SECOND forbidden dependency unnoticed. Sorted, one pair per line.
    ///     <para>
    ///         78 pairs across 64 endpoint types today: 43 a persistence store, 34 a concrete provider's contract or
    ///         options type, and one an <c>AI.Agent</c> tool policy — that last one is a dependency the rule's allow
    ///         list does not name, caught only because a leaf outside the allow list is a violation in its own right
    ///         rather than something the forbid list has to have anticipated.
    ///     </para>
    ///     <para>
    ///         Slice S6 deletes entries from this list as it migrates each area behind a
    ///         <c>Client.Application</c> service, and deletes the list itself with the last pair.
    ///     </para>
    /// </summary>
    private static readonly string[] AllowedViolations =
    [
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.CancelBenchmarkRunEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ClearBenchmarkFidelityCacheEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ClearBenchmarkRunScoreEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.CompareBenchmarkCellsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.CreateBenchmarkProjectEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.DeleteBenchmarkProjectEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.DeleteBenchmarkRunEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.GetBenchmarkKldDiskEstimateEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.GetBenchmarkPairwiseEstimateEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.GetBenchmarkProjectEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.GetBenchmarkRunEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ListBenchmarkCellsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ListBenchmarkComparisonsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ListBenchmarkFidelityAttemptsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ListBenchmarkProjectsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ListBenchmarkRunsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ListBenchmarkTaskItemsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.RejudgeBenchmarkProjectEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.RejudgeBenchmarkRunEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ReorderBenchmarkTaskItemsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.ScoreBenchmarkRunEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.StartBenchmarkRunFidelityEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.UpdateBenchmarkJudgePolicyEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.UpdateBenchmarkProjectEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.UpdateBenchmarkProjectFidelityEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IBenchmarkStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.ArchiveDevWorkflowDefinitionEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.CreateDevWorkflowDefinitionEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.CreateDevWorkflowRuleSetEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.CreateDevWorkflowWorkItemEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.DeleteDevWorkflowRuleSetEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.GetDevWorkflowArtifactContentEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.GetDevWorkflowDefinitionEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.GetDevWorkflowRuleSetEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.GetDevWorkflowWorkItemEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.ListDevWorkflowArtifactsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.ListDevWorkflowDefinitionsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.ListDevWorkflowRuleSetsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.ListDevWorkflowRunEventsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.ListDevWorkflowRunsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.ListDevWorkflowWorkItemsEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.UpdateDevWorkflowDefinitionEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.UpdateDevWorkflowRuleSetEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.UpdateDevWorkflowWorkItemEndpoint|XE_Local_AI_Engine.Client.Persistence.Stores.IDevWorkflowStore",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.CancelStableDiffusionCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IStableDiffusionCppSourceBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.CreateImageJobEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IImageRuntimeActivityGate",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.EjectImageRuntimeEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IImageServerSupervisor",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.EjectImageRuntimeEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IStableDiffusionInstalledRuntimeStore",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.GetImageRuntimeStatusEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IImageRuntimeActivityGate",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.GetImageRuntimeStatusEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IStableDiffusionInstalledRuntimeStore",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.GetStableDiffusionCppSourceBuildPrerequisitesEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IStableDiffusionCppSourceBuildPrerequisiteProbe",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.GetStableDiffusionCppSourceBuildStatusEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IStableDiffusionCppSourceBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.RemoveStableDiffusionCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IImageRuntimeActivityGate",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.RemoveStableDiffusionCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IStableDiffusionCppSourceBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.RemoveStableDiffusionCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IStableDiffusionInstalledRuntimeStore",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.StartStableDiffusionCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IImageRuntimeActivityGate",
        "XE_Local_AI_Engine.Client.Endpoints.Images.V1.StartStableDiffusionCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts.IStableDiffusionCppSourceBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.GetToolCatalogEndpoint|XE_Local_AI_Engine.AI.Agent.Tools.IToolApprovalPolicy",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.CancelCudaBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ICudaBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.CancelLlamaCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppSourceBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.EjectRunningModelEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaServerProcessSupervisor",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.GetCudaBuildPrerequisitesEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ICudaBuildPrerequisiteProbe",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.GetCudaBuildStatusEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ICudaBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.GetLlamaCppSourceBuildPrerequisitesEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppSourceBuildPrerequisiteProbe",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.GetLlamaCppSourceBuildStatusEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppSourceBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.ListRunningModelsEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaServerProcessSupervisor",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveCudaBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.IInstalledRuntimeStore",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveCudaBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppBinaryManager",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveCudaBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppSourceBuildActivity",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveCudaBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppUpdateState",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveCudaBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaServerProcessSupervisor",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveLlamaCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.IInstalledRuntimeStore",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveLlamaCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppBinaryManager",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveLlamaCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppSourceBuildActivity",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveLlamaCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppUpdateState",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.RemoveLlamaCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaServerProcessSupervisor",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.StartCudaBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ICudaBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.StartCudaBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppSourceBuildService",
        "XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.StartLlamaCppSourceBuildEndpoint|XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaCppSourceBuildService",
    ];

    [Test]
    public void EndpointConstructors_TakeNoForbiddenDependency()
    {
        var (violations, endpointCount) = Scan();

        AssertEx.True(endpointCount >= 400,
            $"Expected the host's known endpoint surface; found {endpointCount} endpoint types. The assembly marker or the reflection scan is broken.");

        var unlisted = violations.Where(pair => !AllowedViolations.Contains(pair, StringComparer.Ordinal)).ToList();

        AssertEx.Empty(unlisted,
            "An endpoint may take constructor parameters only from Client.Application, AI.Contracts, "
            + "Providers.Abstractions, non-endpoint host types, Microsoft.*, System.* and FastEndpoints.*. Take the "
            + "dependency below through an application-layer service instead of adding it to the ratchet list:"
            + Environment.NewLine + string.Join(Environment.NewLine, unlisted));
    }

    [Test]
    public void EveryAllowlistedPair_StillViolatesToday()
    {
        var (violations, endpointCount) = Scan();

        AssertEx.True(endpointCount >= 400,
            $"Expected the host's known endpoint surface; found {endpointCount} endpoint types. The assembly marker or the reflection scan is broken.");

        AssertEx.Equal(AllowedViolations.Length, AllowedViolations.Distinct(StringComparer.Ordinal).Count(),
            "The ratchet list has a duplicate pair, so deleting one line would leave the exemption standing.");
        AssertEx.True(AllowedViolations.SequenceEqual(AllowedViolations.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "The ratchet list is kept sorted, one pair per line, so a diff to it reads as one removed line per migrated site.");

        var stale = AllowedViolations.Where(pair => !violations.Contains(pair, StringComparer.Ordinal)).ToList();

        AssertEx.Empty(stale,
            "These pairs are on the ratchet list but no longer exist — the dependency was removed and the exemption "
            + "outlived it. Delete the line(s) below so the list keeps shrinking towards zero:"
            + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    private static (IReadOnlyList<string> Violations, int EndpointCount) Scan()
    {
        var endpointTypes = ClientAssembly.GetTypes()
                                          .Where(type => type.IsClass && !type.IsAbstract && typeof(BaseEndpoint).IsAssignableFrom(type))
                                          .ToList();
        var endpointNames = endpointTypes.Select(type => type.FullName ?? type.Name).ToHashSet(StringComparer.Ordinal);

        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpointTypes)
        {
            foreach (var constructor in endpoint.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    foreach (var dependency in Flatten(parameter.ParameterType))
                    {
                        // An endpoint naming itself is not a dependency on itself: every endpoint that logs takes an
                        // ILogger<TSelf>, where the type argument is a logging CATEGORY, not something resolved and
                        // called. Skipping the self-reference keeps the "an endpoint may not take ANOTHER endpoint"
                        // half of the host-namespace rule intact.
                        if (dependency != endpoint && IsForbidden(dependency, endpointNames))
                        {
                            violations.Add($"{endpoint.FullName}|{NameOf(dependency)}");
                        }
                    }
                }
            }
        }

        return (violations.ToList(), endpointTypes.Count);
    }

    /// <summary>
    ///     Every type a parameter declaration actually depends on: the declared type plus, recursively, its generic
    ///     arguments, its array/by-ref element type and <c>Nullable&lt;T&gt;</c>'s underlying type. Open generic
    ///     parameters are skipped — they name no concrete dependency.
    /// </summary>
    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.IsGenericParameter)
        {
            yield break;
        }

        if (type.HasElementType)
        {
            var element = type.GetElementType();
            if (element is not null)
            {
                foreach (var nested in Flatten(element))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        yield return type;

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }

    private static bool IsForbidden(Type type, IReadOnlySet<string> endpointNames)
    {
        var name = NameOf(type);
        if (ForbiddenTypes.Contains(name, StringComparer.Ordinal))
        {
            return true;
        }

        var space = type.Namespace ?? string.Empty;
        if (ForbiddenNamespaces.Any(forbidden => IsUnder(space, forbidden)))
        {
            return true;
        }

        // Every concrete provider is forbidden; Providers.Abstractions is the one seam the host may bind to.
        if (IsUnder(space, ProvidersNamespace) && !IsUnder(space, AbstractionsNamespace))
        {
            return true;
        }

        // A host type is composition surface — unless it is itself an endpoint, which would make one route's
        // handler a dependency of another's.
        if (IsUnder(space, ClientNamespace))
        {
            return endpointNames.Contains(name);
        }

        return !AllowedNamespaces.Any(allowed => IsUnder(space, allowed));
    }

    /// <summary>
    ///     Namespace containment on segment boundaries. A plain <c>StartsWith</c> would put
    ///     <c>…Providers.OpenAICompatible.Core</c> under <c>…Providers.OpenAICompat</c>.
    /// </summary>
    private static bool IsUnder(string space, string root) =>
        string.Equals(space, root, StringComparison.Ordinal) || space.StartsWith(root + ".", StringComparison.Ordinal);

    /// <summary>
    ///     The stable key half for a dependency: the namespace-qualified name with the CLR's generic arity suffix and
    ///     any nested-type <c>+</c> left as-is, so the key is greppable and unambiguous.
    /// </summary>
    private static string NameOf(Type type)
    {
        // A constructed generic's FullName carries the assembly-qualified argument list; the open definition's name
        // is what a human greps for, and the arguments are visited separately by Flatten anyway.
        var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        return definition.FullName ?? (definition.Namespace is null ? definition.Name : $"{definition.Namespace}.{definition.Name}");
    }
}
