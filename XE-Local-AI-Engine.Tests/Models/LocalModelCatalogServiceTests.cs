namespace XE_Local_AI_Engine.Tests.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OllamaSharp.Models;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The catalog's four sources fail independently, and that policy — not the list endpoint's mapping — is what
///     keeps a node with no Ollama, no cloud session and an unreadable GGUF registry able to answer a picker query.
///     Each test kills exactly one source and asserts the others survive it.
/// </summary>
public sealed class LocalModelCatalogServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task GetCatalog_WhenEverySourceAnswers_CarriesAllOfThem()
    {
        var harness = new Harness();
        harness.WithOllamaModels("orca-mini:latest");
        harness.WithInstalledGguf("local/gguf:Q4_K_M");
        harness.WithCodexSession(Now.AddHours(1));
        harness.WithAzureDeployment("gpt-5");

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, AssertEx.NotNull(catalog.OllamaModels).Count);
        AssertEx.Equal(expected: 1, catalog.InstalledGgufModels.Count);
        AssertEx.True(catalog.HasUsableCodexSession);
        AssertEx.NotNull(catalog.AzureFoundryConnection);
        AssertEx.Equal("selected:model", catalog.SelectedModelName);
        AssertEx.Equal(harness.ConfiguredDefaultModel, catalog.ConfiguredDefaultModelName);
    }

    [Test]
    public async Task GetCatalog_WhenOllamaIsUnreachable_ReportsNullModelsAndKeepsTheOtherSources()
    {
        // Null models — not an empty list — is the unavailability signal the list endpoint degrades on. An empty list
        // would render as "available, nothing installed" and hide the outage.
        var harness = new Harness();
        harness.ModelService.ListLocalModelsAsync(Arg.Any<CancellationToken>())
               .Returns<Task<IEnumerable<Model>>>(_ => throw new HttpRequestException("connection refused"));
        harness.WithInstalledGguf("local/gguf:Q4_K_M");
        harness.WithCodexSession(Now.AddHours(1));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Null(catalog.OllamaModels);
        AssertEx.Empty(catalog.Classifications);
        AssertEx.Equal(expected: 1, catalog.InstalledGgufModels.Count);
        AssertEx.True(catalog.HasUsableCodexSession);
    }

    [Test]
    public async Task GetCatalog_WhenTheGgufRegistryFails_DegradesToNoGgufEntriesOnly()
    {
        var harness = new Harness();
        harness.WithOllamaModels("orca-mini:latest");
        harness.GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
               .Returns<Task<IReadOnlyList<LocalModelDescriptor>>>(_ => throw new IOException("registry unreadable"));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.Empty(catalog.InstalledGgufModels);
        AssertEx.Equal(expected: 1, AssertEx.NotNull(catalog.OllamaModels).Count);
    }

    [Test]
    public async Task GetCatalog_WhenTheCodexSessionIsExpired_OffersNoCodexModels()
    {
        // Same skew-adjusted gate cloud/codex/status uses: a stored-but-stale session must not put Codex ids in the
        // picker, because selecting one would fail on the first send.
        var harness = new Harness();
        harness.WithCodexSession(Now.AddSeconds(30));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(catalog.HasUsableCodexSession);
    }

    [Test]
    public async Task GetCatalog_WhenTheCodexStoreThrows_OffersNoCodexModelsRatherThanFailing()
    {
        var harness = new Harness();
        harness.WithOllamaModels("orca-mini:latest");
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns<Task<CodexTokens?>>(_ => throw new InvalidOperationException("token store unreadable"));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(catalog.HasUsableCodexSession);
        AssertEx.Equal(expected: 1, AssertEx.NotNull(catalog.OllamaModels).Count);
    }

    private sealed class Harness
    {
        public string ConfiguredDefaultModel { get; } = new LocalChatAgentOptions().DefaultModel;

        public IOllamaModelService ModelService { get; } = Substitute.For<IOllamaModelService>();

        public IGgufModelStore GgufModelStore { get; } = Substitute.For<IGgufModelStore>();

        public ICodexTokenStore CodexTokenStore { get; } = Substitute.For<ICodexTokenStore>();

        private IModelClassificationService ClassificationService { get; } = Substitute.For<IModelClassificationService>();

        private ICloudModelResolver CloudModelResolver { get; } = Substitute.For<ICloudModelResolver>();

        private INodeRuntimeSettings RuntimeSettings { get; } = Substitute.For<INodeRuntimeSettings>();

        public Harness()
        {
            RuntimeSettings.GetDefaultModelNameAsync(Arg.Any<CancellationToken>()).Returns("selected:model");
            ModelService.ListLocalModelsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Model>().AsEnumerable());
            GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LocalModelDescriptor>());
            CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
            CloudModelResolver.ResolveAzureFoundryConnectionAsync(Arg.Any<CancellationToken>()).Returns((StoredAzureFoundryConnection?)null);
            ClassificationService.ClassifyAsync(Arg.Any<IEnumerable<ModelIdentity>>(), Arg.Any<CancellationToken>())
                                 .Returns(call => call.Arg<IEnumerable<ModelIdentity>>()
                                                      .ToDictionary(entry => entry.ModelName,
                                                          entry => new ModelClassificationResult(entry.ModelName,
                                                              ModelKind.Chat,
                                                              ModelKind.Chat,
                                                              [],
                                                              IsOverridden: false),
                                                          StringComparer.OrdinalIgnoreCase)
                                                  as IReadOnlyDictionary<string, ModelClassificationResult>);
        }

        public void WithOllamaModels(params string[] modelNames) =>
            ModelService.ListLocalModelsAsync(Arg.Any<CancellationToken>())
                        .Returns(modelNames.Select(static name => new Model
                        {
                            Name = name,
                            ModelName = name
                        }).AsEnumerable());

        public void WithInstalledGguf(string modelName) =>
            GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                          .Returns<IReadOnlyList<LocalModelDescriptor>>([
                              new LocalModelDescriptor
                              {
                                  ModelName = modelName,
                                  ProviderName = "llamacpp",
                                  IsAvailable = true,
                                  SizeBytes = 1024,
                                  ModifiedAt = null,
                                  MaxContextTokens = null
                              }
                          ]);

        public void WithCodexSession(DateTimeOffset expiresUtc) =>
            CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>())
                           .Returns(new CodexTokens("access", "refresh", expiresUtc, "account"));

        public void WithAzureDeployment(string deploymentName) =>
            CloudModelResolver.ResolveAzureFoundryConnectionAsync(Arg.Any<CancellationToken>())
                              .Returns(new StoredAzureFoundryConnection
                              {
                                  Models =
                                  [
                                      new StoredAzureFoundryModel
                                      {
                                          DeploymentName = deploymentName
                                      }
                                  ]
                              });

        public LocalModelCatalogService CreateService() =>
            new(ModelService,
                ClassificationService,
                GgufModelStore,
                RuntimeSettings,
                Options.Create(new LocalChatAgentOptions()),
                CodexTokenStore,
                Options.Create(new CodexOptions()),
                CloudModelResolver,
                new ManualTimeProvider(Now),
                NullLogger<LocalModelCatalogService>.Instance);
    }
}
