namespace XE_Local_AI_Engine.Tests.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The catalog's sources fail independently, and that policy, not the endpoint's mapping, keeps a node with no cloud
///     session and an unreadable GGUF registry able to answer a picker query. Each test kills one.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalModelCatalogServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task GetCatalog_WhenEverySourceAnswers_CarriesAllOfThem()
    {
        var harness = new Harness();
        harness.WithInstalledGguf("local/gguf:Q4_K_M");
        harness.WithCodexSession(Now.AddHours(1));
        harness.WithAzureDeployment("gpt-5");
        harness.WithExternalModel();
        harness.WithOverride("local/gguf:Q4_K_M", ModelKind.Embedding);

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, catalog.InstalledGgufModels.Count);
        AssertEx.True(catalog.HasUsableCodexSession);
        AssertEx.NotNull(catalog.AzureFoundryConnection);
        AssertEx.Equal(expected: 1, catalog.ExternalModels.Count);
        AssertEx.Equal(ModelKind.Embedding, catalog.Classifications["LOCAL/GGUF:Q4_K_M"].OverrideKind);
        AssertEx.Equal("selected:model", catalog.SelectedModelName);
        AssertEx.Equal(harness.ConfiguredDefaultModel, catalog.ConfiguredDefaultModelName);
    }

    [Test]
    public async Task GetCatalog_WhenTheExternalRegistryThrows_DegradesToNoExternalEntriesOnly()
    {
        // An unreadable encrypted external store must not take the local picker down with it: the other sources
        // have nothing to do with it, and a node whose external file is corrupt still has its GGUFs.
        var harness = new Harness();
        harness.WithInstalledGguf("local/gguf:Q4_K_M");
        harness.ExternalProviderRegistry.ListRegistrationsAsync(Arg.Any<CancellationToken>())
               .Returns<Task<IReadOnlyList<ExternalProviderModelRegistration>>>(_ => throw new IOException("external store unreadable"));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None);

        AssertEx.Empty(catalog.ExternalModels);
        AssertEx.Equal(expected: 1, catalog.InstalledGgufModels.Count);
    }

    [Test]
    public async Task GetCatalog_WhenTheClassificationStoreFails_LosesTheOverridesAndKeepsTheOtherSources()
    {
        var harness = new Harness();
        harness.ClassificationStore.ListAsync(Arg.Any<CancellationToken>())
               .Returns<Task<IReadOnlyList<ModelClassificationRecord>>>(_ => throw new InvalidOperationException("database unreadable"));
        harness.WithInstalledGguf("local/gguf:Q4_K_M");
        harness.WithCodexSession(Now.AddHours(1));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None);

        AssertEx.Empty(catalog.Classifications);
        AssertEx.Equal(expected: 1, catalog.InstalledGgufModels.Count);
        AssertEx.True(catalog.HasUsableCodexSession);
    }

    [Test]
    public async Task GetCatalog_WhenTheGgufRegistryFails_DegradesToNoGgufEntriesOnly()
    {
        var harness = new Harness();
        harness.WithCodexSession(Now.AddHours(1));
        harness.GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
               .Returns<Task<IReadOnlyList<LocalModelDescriptor>>>(_ => throw new IOException("registry unreadable"));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None);

        AssertEx.Empty(catalog.InstalledGgufModels);
        AssertEx.True(catalog.HasUsableCodexSession);
    }

    [Test]
    public async Task GetCatalog_WhenTheCodexSessionIsExpired_OffersNoCodexModels()
    {
        // Same skew-adjusted gate cloud/codex/status uses: a stored-but-stale session must not put Codex ids in the
        // picker, because selecting one would fail on the first send.
        var harness = new Harness();
        harness.WithCodexSession(Now.AddSeconds(30));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None);

        AssertEx.False(catalog.HasUsableCodexSession);
    }

    [Test]
    public async Task GetCatalog_WhenTheCodexStoreThrows_OffersNoCodexModelsRatherThanFailing()
    {
        var harness = new Harness();
        harness.WithInstalledGguf("local/gguf:Q4_K_M");
        harness.CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>())
               .Returns<Task<CodexTokens?>>(_ => throw new InvalidOperationException("token store unreadable"));

        var catalog = await harness.CreateService().GetCatalogAsync(CancellationToken.None);

        AssertEx.False(catalog.HasUsableCodexSession);
        AssertEx.Equal(expected: 1, catalog.InstalledGgufModels.Count);
    }

    private sealed class Harness
    {
        public string ConfiguredDefaultModel { get; } = new LocalChatAgentOptions().DefaultModel;

        public IGgufModelStore GgufModelStore { get; } = Substitute.For<IGgufModelStore>();

        public ICodexTokenStore CodexTokenStore { get; } = Substitute.For<ICodexTokenStore>();

        public IExternalProviderRegistry ExternalProviderRegistry { get; } = Substitute.For<IExternalProviderRegistry>();

        public IModelClassificationStore ClassificationStore { get; } = Substitute.For<IModelClassificationStore>();

        private ICloudModelResolver CloudModelResolver { get; } = Substitute.For<ICloudModelResolver>();

        private INodeRuntimeSettings RuntimeSettings { get; } = Substitute.For<INodeRuntimeSettings>();

        public Harness()
        {
            RuntimeSettings.GetDefaultModelNameAsync(Arg.Any<CancellationToken>()).Returns("selected:model");
            ClassificationStore.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ModelClassificationRecord>());
            GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LocalModelDescriptor>());
            CodexTokenStore.LoadAsync(Arg.Any<CancellationToken>()).Returns((CodexTokens?)null);
            CloudModelResolver.ResolveAzureFoundryConnectionAsync(Arg.Any<CancellationToken>()).Returns((StoredAzureFoundryConnection?)null);
            ExternalProviderRegistry.ListRegistrationsAsync(Arg.Any<CancellationToken>())
                                    .Returns<IReadOnlyList<ExternalProviderModelRegistration>>([]);
        }

        public void WithOverride(string modelName, ModelKind kind) =>
            ClassificationStore.ListAsync(Arg.Any<CancellationToken>())
                               .Returns<IReadOnlyList<ModelClassificationRecord>>([
                                   new ModelClassificationRecord
                                   {
                                       ModelName = modelName,
                                       Digest = null,
                                       DetectedKind = ModelKind.Unknown,
                                       DetectedCapabilitiesJson = null,
                                       OverrideKind = kind,
                                       DetectedAtUtc = null,
                                       UpdatedAtUtc = 1
                                   }
                               ]);

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

        public void WithExternalModel() =>
            ExternalProviderRegistry.ListRegistrationsAsync(Arg.Any<CancellationToken>())
                                    .Returns<IReadOnlyList<ExternalProviderModelRegistration>>([
                                        new ExternalProviderModelRegistration
                                        {
                                            Connection = new ExternalProviderConnectionDescriptor
                                            {
                                                Id = "unsloth-box",
                                                DisplayName = "Unsloth box",
                                                BaseUrl = new Uri("http://127.0.0.1:18099/v1/"),
                                                Locality = ExternalProviderLocality.Local,
                                                CloudGrants = ExternalProviderCloudGrants.None
                                            },
                                            Model = new ExternalProviderModelDescriptor
                                            {
                                                WireId = "qwen3-27b"
                                            }
                                        }
                                    ]);

        public LocalModelCatalogService CreateService() =>
            new(ClassificationStore,
                GgufModelStore,
                RuntimeSettings,
                Options.Create(new LocalChatAgentOptions()),
                CodexTokenStore,
                Options.Create(new CodexOptions()),
                CloudModelResolver,
                ExternalProviderRegistry,
                new ManualTimeProvider(Now),
                NullLogger<LocalModelCatalogService>.Instance);
    }
}
