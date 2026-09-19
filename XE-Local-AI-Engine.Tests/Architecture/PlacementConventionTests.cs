namespace XE_Local_AI_Engine.Tests.Architecture;

using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Extensions;
using ArchUnitNET.Loader;
using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Providers.Capabilities;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Tests.Testing;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
// ArchUnitNET.Domain declares its own Assembly type; every Assembly below is the CLR one.
using Assembly = System.Reflection.Assembly;

/// <summary>
///     Pins WHERE a type is allowed to live inside a project. This is the axis orthogonal to
///     <see cref="LayerDependencyTests" />, which pins which assembly may reference which; neither rule here says
///     anything about dependency direction. It is also the axis IDE0130 cannot reach: that analyzer only checks that a
///     file's namespace matches the folder the file already sits in, so a misfiled type with a matching namespace is
///     invisible to it.
///     <para>
///         Rules A and B are ArchUnitNET rules over compiled IL. Rule C is a source-text scan, because "a DTO must not
///         be declared in the same FILE as a service" is a statement about files and IL carries no file identity; the
///         same technique is used by <see cref="ProviderTelemetryWrapGuardTests" />.
///     </para>
///     <para>
///         Only the ArchUnitNET core package is referenced. The first-party TngTech.ArchUnitNET.TUnit adapter resolves
///         cleanly here (its TUnit.Assertions 0.52.51 floor is satisfied by this repo's 1.65.68 pin) but buys one line
///         per rule at the cost of a second package plus a 0.52-compiled assembly binding against 1.65 at run time.
///         Evaluating the rule and reporting through <see cref="AssertEx" /> matches how
///         <see cref="LayerDependencyTests" /> already reports NetArchTest results.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class PlacementConventionTests
{
    // Marker types anchor each assembly so the rules run over the real compiled IL, not a namespace string. These
    // mirror LayerDependencyTests' markers deliberately: one marker set, one place to fix when a type is renamed.
    private static readonly Assembly ClientAssembly = typeof(LocalApiRoutes).Assembly;

    // Providers.Abstractions is absent on purpose and is NOT an exception to Rule A: that assembly IS the shared
    // contracts layer, its root namespace is where its ~30 public seam interfaces belong, and a `.Contracts`
    // sub-namespace inside a contracts assembly would be a tautology. Every other provider is a concrete
    // implementation assembly, where the seam it publishes is the part that must be findable.
    private static readonly Assembly[] ProviderAssemblies =
    [
        typeof(CapabilitiesServiceCollectionExtensions).Assembly,
        typeof(ICodexOAuthChatClientFactory).Assembly,
        typeof(HuggingFaceOptions).Assembly,
        typeof(IInstalledRuntimeStore).Assembly,
        typeof(OllamaLocalModelProvider).Assembly,
        typeof(ExternalOpenAiModelProvider).Assembly,
        typeof(OpenAICompatibleClientFactory).Assembly,
        typeof(IStableDiffusionBinaryManager).Assembly,
        typeof(ITrainingRuntimeService).Assembly,
        // Anchored on a public static class rather than the binary-manager interface the other providers use: the
        // project was enrolled here in the commit that added it to the solution, before that interface existed. The
        // marker stays as it is — it identifies the assembly just as well, and swapping it would churn this list for
        // no gain.
        typeof(WhisperCppReleasePins).Assembly
    ];

    // FastEndpoints and FluentValidation are loaded as well, and are NOT under test: AreAssignableTo(Type) resolves
    // the base type inside the architecture, so a rule about "types deriving from BaseEndpoint" throws
    // TypeDoesNotExistInArchitecture unless the assembly declaring BaseEndpoint was loaded. Every rule below still
    // scopes itself with ResideInAssembly, so nothing in these two assemblies is ever asserted over.
    private static readonly Architecture Architecture =
        new ArchLoader()
            .LoadAssemblies([ClientAssembly, typeof(BaseEndpoint).Assembly, typeof(IValidator).Assembly, .. ProviderAssemblies])
            .Build();

    [Test]
    public void ProviderPublicInterfaces_ResideInTheProviderContractsNamespace()
    {
        var publicInterfaces = ProviderAssemblies.Sum(assembly => assembly.GetExportedTypes().Count(type => type.IsInterface));

        // Non-vacuity: a broken marker or an empty load would otherwise report "no violations" as a pass. Raised from
        // fifty once the whisper.cpp provider's contract surface was complete; the real count is seventy-seven, and
        // the floor sits a little under it because it exists to catch a loader that returned nothing, not to notice
        // that one interface was refactored away.
        AssertEx.True(publicInterfaces >= 70,
            $"Expected at least seventy public provider interfaces to scan; found {publicInterfaces}. The assembly markers or the loader are broken.");

        foreach (var assembly in ProviderAssemblies)
        {
            var rootNamespace = RootNamespaceOf(assembly);

            AssertNoViolations(Interfaces().That().ArePublic().And().ResideInAssembly(assembly)
                                           .Should().ResideInNamespace($"{rootNamespace}.Contracts").WithoutRequiringPositiveResults(),
                $"Public interfaces in '{assembly.GetName().Name}' are the seam other layers bind to and must live in "
                + $"'{rootNamespace}.Contracts', not next to the implementation that happens to satisfy them today.");
        }
    }

    [Test]
    public void ProviderOptionsClasses_ResideInTheProviderOptionsNamespace()
    {
        var optionsClasses = ProviderAssemblies.Sum(assembly =>
            assembly.GetExportedTypes().Count(type => type.IsClass && type.Name.EndsWith("Options", StringComparison.Ordinal)));

        // Raised from eight alongside the interface floor above, for the same reason: the real count is twelve.
        AssertEx.True(optionsClasses >= 11,
            $"Expected at least eleven public provider *Options classes to scan; found {optionsClasses}. The assembly markers or the loader are broken.");

        foreach (var assembly in ProviderAssemblies)
        {
            var rootNamespace = RootNamespaceOf(assembly);

            AssertNoViolations(Classes().That().ArePublic().And().ResideInAssembly(assembly).And().HaveNameEndingWith("Options")
                                        .Should().ResideInNamespace($"{rootNamespace}.Options").WithoutRequiringPositiveResults(),
                $"Bound configuration in '{assembly.GetName().Name}' must live in '{rootNamespace}.Options' so the "
                + "full set a provider binds is enumerable from one folder.");
        }
    }

    [Test]
    public void ClientEndpointsMappersAndValidators_ResideInTheirVersionedNamespaces()
    {
        var exported = ClientAssembly.GetTypes();
        var endpoints = exported.Count(type => type.IsClass && typeof(BaseEndpoint).IsAssignableFrom(type));
        var mappers = exported.Count(type => type.IsClass && type.Name.EndsWith("Mapper", StringComparison.Ordinal));
        var validators = exported.Count(type => type.IsClass && typeof(IValidator).IsAssignableFrom(type));

        AssertEx.True(endpoints >= 300 && mappers >= 40 && validators >= 55,
            $"Expected the host's known endpoint surface; found {endpoints} endpoints, {mappers} mappers, {validators} validators. The scan is broken.");

        AssertNoViolations(Classes().That().AreAssignableTo(typeof(BaseEndpoint)).And().ResideInAssembly(ClientAssembly)
                                    .Should().ResideInNamespaceMatching(@"^.*\.V1$").WithoutRequiringPositiveResults(),
            "Every FastEndpoints endpoint in the host is part of the versioned local API and must sit in a '.V1' "
            + "namespace; an unversioned endpoint namespace is a route whose contract nothing pins.");

        AssertNoViolations(Classes().That().HaveNameEndingWith("Mapper").And().ResideInAssembly(ClientAssembly)
                                    .Should().ResideInNamespaceMatching(@"^.*\.V1\.Mappers$").WithoutRequiringPositiveResults(),
            "Endpoint DTO mappers belong in '<area>.V1.Mappers'. Keeping them out of the endpoint file is what stops "
            + "the mapping from being re-derived per endpoint.");

        AssertNoViolations(Classes().That().AreAssignableTo(typeof(IValidator)).And().ResideInAssembly(ClientAssembly)
                                    .Should().ResideInNamespaceMatching(@"^.*\.V1\.Validators$").WithoutRequiringPositiveResults(),
            "Request validators belong in '<area>.V1.Validators'. FastEndpoints resolves them by convention, so a "
            + "validator filed elsewhere is found by the framework and by nobody reading the folder.");
    }

    private static string RootNamespaceOf(Assembly assembly) =>
        (assembly.GetName().Name ?? throw new InvalidOperationException("Assembly has no simple name.")).Replace('-', '_');

    // Every rule above ends in WithoutRequiringPositiveResults(): ArchUnitNET otherwise fails a rule whose predicate
    // matched nothing. That default is a non-vacuity guard, but a per-assembly one — Providers.Ollama declares no
    // *Options class and never will — and honouring it would mean an allow-list of "assemblies exempt from a rule they
    // trivially satisfy". Each test instead asserts an explicit floor on how many types the whole scan saw, which is
    // the guard that actually catches a broken marker or an empty load.
    private static void AssertNoViolations(IArchRule rule, string because)
    {
        if (rule.HasNoViolations(Architecture))
        {
            return;
        }

        AssertEx.True(false, $"{because}{Environment.NewLine}{rule.Evaluate(Architecture).ToErrorMessage()}");
    }
}
