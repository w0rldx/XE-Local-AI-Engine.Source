namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using NetArchTest.Rules;
using XE_Local_AI_Engine.AI.Contracts.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Capabilities;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
/// Freezes the layer-dependency direction of the node solution so that an
/// accidental upward reference (a leaf provider reaching back into the host,
/// application, or persistence layer) fails the build instead of shipping.
/// </summary>
public sealed class LayerDependencyTests
{
    // Namespace roots used as forbidden dependency targets. NetArchTest matches
    // on the full type namespace, and the host (Client) and application
    // (Client.Application) assemblies share the same "XE_Local_AI_Engine.Client"
    // root namespace, so that single prefix covers both. Persistence lives under
    // the distinct "...Client.Persistence" sub-namespace.
    private const string ClientNamespace = "XE_Local_AI_Engine.Client";
    private const string PersistenceNamespace = "XE_Local_AI_Engine.Client.Persistence";
    private const string ApplicationNamespace = "XE_Local_AI_Engine.Client.Application";

    private const string OllamaNamespace = "XE_Local_AI_Engine.Providers.Ollama";
    private const string LlamaServerNamespace = "XE_Local_AI_Engine.Providers.LlamaServer";
    private const string HuggingFaceNamespace = "XE_Local_AI_Engine.Providers.HuggingFace";
    private const string CodexOAuthNamespace = "XE_Local_AI_Engine.Providers.CodexOAuth";
    private const string CapabilitiesNamespace = "XE_Local_AI_Engine.Providers.Capabilities";
    private const string AbstractionsNamespace = "XE_Local_AI_Engine.Providers.Abstractions";

    // Marker types anchor each assembly so we test the real compiled IL, not a
    // namespace string. Every marker is a verified public type in its assembly.
    private static readonly Assembly OllamaAssembly = typeof(OllamaLocalModelProvider).Assembly;
    private static readonly Assembly LlamaServerAssembly = typeof(IInstalledRuntimeStore).Assembly;
    private static readonly Assembly HuggingFaceAssembly = typeof(HuggingFaceOptions).Assembly;
    private static readonly Assembly CodexOAuthAssembly = typeof(ICodexOAuthChatClientFactory).Assembly;
    private static readonly Assembly CapabilitiesAssembly = typeof(CapabilitiesServiceCollectionExtensions).Assembly;
    private static readonly Assembly AbstractionsAssembly = typeof(ILocalModelProvider).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(MessageRole).Assembly;

    [Test]
    public void OllamaProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertNoDependency(
            OllamaAssembly,
            OllamaNamespace,
            ClientNamespace,
            PersistenceNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void LlamaServerProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertNoDependency(
            LlamaServerAssembly,
            LlamaServerNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void HuggingFaceProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertNoDependency(
            HuggingFaceAssembly,
            HuggingFaceNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void CodexOAuthProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertNoDependency(
            CodexOAuthAssembly,
            CodexOAuthNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CapabilitiesNamespace);
    }

    [Test]
    public void CapabilitiesProvider_DoesNotDependOnApplicationPersistenceHostOrSiblingProviders()
    {
        AssertNoDependency(
            CapabilitiesAssembly,
            CapabilitiesNamespace,
            ClientNamespace,
            PersistenceNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace);
    }

    [Test]
    public void AiContracts_DoesNotDependOnApplicationProvidersPersistenceOrHost()
    {
        AssertNoDependency(
            ContractsAssembly,
            "XE_Local_AI_Engine.AI.Contracts",
            ClientNamespace,
            PersistenceNamespace,
            ApplicationNamespace,
            "XE_Local_AI_Engine.Providers");
    }

    [Test]
    public void ProvidersAbstractions_DoesNotDependOnConcreteProvidersApplicationPersistenceOrHost()
    {
        AssertNoDependency(
            AbstractionsAssembly,
            AbstractionsNamespace,
            ClientNamespace,
            PersistenceNamespace,
            ApplicationNamespace,
            OllamaNamespace,
            LlamaServerNamespace,
            HuggingFaceNamespace,
            CodexOAuthNamespace,
            CapabilitiesNamespace);
    }

    /// <summary>
    /// Asserts that no type residing in <paramref name="sourceNamespace"/> within
    /// <paramref name="assembly"/> has a dependency on any of the
    /// <paramref name="forbiddenNamespaces"/>. Reports the offending type names on failure.
    /// </summary>
    private static void AssertNoDependency(
        Assembly assembly,
        string sourceNamespace,
        params string[] forbiddenNamespaces)
    {
        var result = Types
            .InAssembly(assembly)
            .That()
            .ResideInNamespaceStartingWith(sourceNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        var failing = result.FailingTypeNames is null
            ? string.Empty
            : string.Join(", ", result.FailingTypeNames);

        AssertEx.True(
            result.IsSuccessful,
            $"Types in '{sourceNamespace}' must not depend on [{string.Join(", ", forbiddenNamespaces)}]. "
                + $"Violating types: {failing}");
    }
}
