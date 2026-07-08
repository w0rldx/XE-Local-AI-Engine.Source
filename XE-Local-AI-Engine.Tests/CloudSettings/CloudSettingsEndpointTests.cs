namespace XE_Local_AI_Engine.Tests.CloudSettings;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class CloudSettingsEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetCloudSettings_WhenCredentialsExist_ReturnsSecretSafeMetadata()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateConfig());
        await using var factory = CreateFactory(cloudCredentialStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/cloud-settings");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var settings = Deserialize<CloudSettingsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("AzureFoundry", settings.ProviderName);
        AssertEx.NotNull(settings.AzureFoundry);
        AssertEx.Equal("https://example.openai.azure.com/", settings.AzureFoundry!.Endpoint);
        AssertEx.Equal("gpt-4o", settings.AzureFoundry.Models.Single().DeploymentName);
        AssertEx.True(settings.AzureFoundry.HasStoredApiKey);
        AssertEx.False(body.Contains("test-api-key", StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveCloudSettings_WhenValid_SavesReportsAndDoesNotReturnApiKey()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "ApiKey",
            ApiKey = "new-secret-api-key",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o-mini"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var settings = Deserialize<CloudSettingsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.NotNull(settings.AzureFoundry);
        AssertEx.Equal("gpt-4o-mini", settings.AzureFoundry!.Models.Single().DeploymentName);
        AssertEx.True(settings.AzureFoundry.HasStoredApiKey);
        AssertEx.False(body.Contains("new-secret-api-key", StringComparison.Ordinal));
        await cloudCredentialStore.Received(1).SaveConfigAsync(Arg.Is<StoredCloudProviderConfig>(config =>
                config.AzureFoundry != null
                && config.AzureFoundry.ApiKey == "new-secret-api-key"
                && config.AzureFoundry.Models.Any(model => model.DeploymentName == "gpt-4o-mini")),
            Arg.Any<CancellationToken>());
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenEndpointIsNotHttps_ReturnsValidationProblem()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "http://example.openai.azure.com/",
            AuthMode = "ApiKey",
            ApiKey = "secret",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await cloudCredentialStore.DidNotReceiveWithAnyArgs().SaveConfigAsync(Arg.Any<StoredCloudProviderConfig>(), Arg.Any<CancellationToken>());
        await capabilityReporter.DidNotReceiveWithAnyArgs().ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearCloudSettings_ClearsCredentialsAndReportsCapabilities()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Delete, "/api/local/v1/cloud-settings");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var settings = await ReadJsonAsync<CloudSettingsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("None", settings.ProviderName);
        AssertEx.False(settings.AzureFoundry?.HasStoredApiKey == true);
        await cloudCredentialStore.Received(1).ClearAsync(Arg.Any<CancellationToken>());
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCloudSettings_NeverEmitsSecretHeaderValue()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateConfigWithHeaders(new StoredAzureFoundryHeader
            {
                Name = "Ocp-Apim-Subscription-Key",
                Value = "top-secret-value",
                IsSecret = true
            },
            new StoredAzureFoundryHeader
            {
                Name = "X-Tenant",
                Value = "tenant-a",
                IsSecret = false
            }));
        await using var factory = CreateFactory(cloudCredentialStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/cloud-settings");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var settings = Deserialize<CloudSettingsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(body.Contains("top-secret-value", StringComparison.Ordinal));
        var secret = AssertEx.NotNull(settings.AzureFoundry!.Headers.FirstOrDefault(header => header.IsSecret));
        AssertEx.Null(secret.Value);
        AssertEx.True(secret.HasStoredValue);
        var open = AssertEx.NotNull(settings.AzureFoundry.Headers.FirstOrDefault(header => !header.IsSecret));
        AssertEx.Equal("tenant-a", open.Value);
    }

    [Test]
    public async Task SaveCloudSettings_WhenReservedHeaderName_ReturnsValidationProblem()
    {
        await AssertSaveRejectedAsync(new SaveAzureFoundryHeaderRequest
        {
            Name = "Authorization",
            Value = "attacker",
            IsSecret = false
        });
    }

    [Test]
    public async Task SaveCloudSettings_WhenValueHasCrlf_ReturnsValidationProblem()
    {
        await AssertSaveRejectedAsync(new SaveAzureFoundryHeaderRequest
        {
            Name = "X-Inject",
            Value = "a\r\nEvil: 1",
            IsSecret = false
        });
    }

    [Test]
    public async Task SaveCloudSettings_WhenOverCaps_ReturnsValidationProblem()
    {
        var headers = Enumerable.Range(0, AzureFoundryHeaderRules.MaxHeaderCount + 1)
                                .Select(index => new SaveAzureFoundryHeaderRequest
                                {
                                    Name = $"X-H{index}",
                                    Value = "v",
                                    IsSecret = false
                                })
                                .ToArray();
        await AssertSaveRejectedAsync(headers);
    }

    [Test]
    public async Task SaveCloudSettings_WhenSecretHeaderBlankOnEdit_KeepsStoredValue()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateConfigWithHeaders(new StoredAzureFoundryHeader
        {
            Name = "X-Api-Token",
            Value = "stored-secret",
            IsSecret = true
        }));
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var body = await SendSaveAsync(cloudCredentialStore, capabilityReporter,
            new SaveAzureFoundryHeaderRequest
            {
                Name = "X-Api-Token",
                Value = null,
                IsSecret = true
            });

        AssertEx.False(body.Contains("stored-secret", StringComparison.Ordinal));
        await cloudCredentialStore.Received(1).SaveConfigAsync(Arg.Is<StoredCloudProviderConfig>(config =>
                config.AzureFoundry!.Headers.Any(header =>
                    header.Name == "X-Api-Token" && header.Value == "stored-secret" && header.IsSecret)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenSecretToggledOffWithBlankValue_DoesNotResurrectStoredSecret()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateConfigWithHeaders(new StoredAzureFoundryHeader
        {
            Name = "X-Api-Token",
            Value = "stored-secret",
            IsSecret = true
        }));
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var body = await SendSaveAsync(cloudCredentialStore, capabilityReporter,
            new SaveAzureFoundryHeaderRequest
            {
                Name = "X-Api-Token",
                Value = null,
                IsSecret = false
            });

        AssertEx.False(body.Contains("stored-secret", StringComparison.Ordinal));
        await cloudCredentialStore.Received(1).SaveConfigAsync(Arg.Is<StoredCloudProviderConfig>(config =>
                config.AzureFoundry!.Headers.All(header => header.Value != "stored-secret")
                && config.AzureFoundry.Headers.Any(header =>
                    header.Name == "X-Api-Token" && string.IsNullOrEmpty(header.Value) && !header.IsSecret)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenSecretHeaderRenamedWithBlankValue_ReturnsValidationProblem()
    {
        // A renamed secret header has no stored secret of the same name to merge against, so it must be rejected as a
        // clean 400 instead of saving an unresolved secret that later throws in CloudCredentialStore.ValidateConfig.
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateConfigWithHeaders(new StoredAzureFoundryHeader
        {
            Name = "X-Old-Name",
            Value = "stored-secret",
            IsSecret = true
        }));
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(CreateSaveRequest([
            new SaveAzureFoundryHeaderRequest
            {
                Name = "X-New-Name",
                Value = null,
                IsSecret = true
            }
        ]));
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.False(body.Contains("stored-secret", StringComparison.Ordinal));
        await cloudCredentialStore.DidNotReceiveWithAnyArgs().SaveConfigAsync(Arg.Any<StoredCloudProviderConfig>(), Arg.Any<CancellationToken>());
        await capabilityReporter.DidNotReceiveWithAnyArgs().ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetCloudSettings_WhenEntraIdConnectionExists_ReturnsSecretSafeMetadata()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateEntraIdConfig());
        await using var factory = CreateFactory(cloudCredentialStore);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/local/v1/cloud-settings");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var settings = Deserialize<CloudSettingsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("EntraId", settings.AzureFoundry!.AuthMode);
        AssertEx.Equal("tenant-id", settings.AzureFoundry.EntraTenantId);
        AssertEx.Equal("client-id", settings.AzureFoundry.EntraClientId);
        AssertEx.Equal("api://backend/.default", settings.AzureFoundry.EntraTokenScope);
        AssertEx.Equal("ClientSecret", settings.AzureFoundry.EntraSignInMethod);
        AssertEx.True(settings.AzureFoundry.HasStoredEntraClientSecret);
        AssertEx.False(body.Contains("entra-client-secret-value", StringComparison.Ordinal));
    }

    [Test]
    public async Task SaveCloudSettings_WhenAuthModeEntraId_PersistsEntraFieldsAndDoesNotReturnSecret()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "EntraId",
            EntraTenantId = "tenant-id",
            EntraClientId = "client-id",
            EntraClientSecret = "new-entra-secret",
            EntraTokenScope = "api://backend/.default",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var settings = Deserialize<CloudSettingsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("EntraId", settings.AzureFoundry!.AuthMode);
        AssertEx.True(settings.AzureFoundry.HasStoredEntraClientSecret);
        // A client secret was supplied, so the sign-in method is coerced to ClientSecret regardless of the request's
        // default (DeviceCode) — the Locked build contract's "derive default: secret present -> ClientSecret".
        AssertEx.Equal("ClientSecret", settings.AzureFoundry.EntraSignInMethod);
        AssertEx.False(body.Contains("new-entra-secret", StringComparison.Ordinal));
        await cloudCredentialStore.Received(1).SaveConfigAsync(Arg.Is<StoredCloudProviderConfig>(config =>
                config.AzureFoundry != null
                && config.AzureFoundry.AuthMode == AzureFoundryAuthMode.EntraId
                && config.AzureFoundry.EntraTenantId == "tenant-id"
                && config.AzureFoundry.EntraClientId == "client-id"
                && config.AzureFoundry.EntraClientSecret == "new-entra-secret"
                && config.AzureFoundry.EntraTokenScope == "api://backend/.default"
                && config.AzureFoundry.EntraSignInMethod == EntraSignInMethod.ClientSecret
                // Managed identity / Entra ID carry no API key.
                && config.AzureFoundry.ApiKey == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenEntraIdRequestsDeviceCodeSignIn_PersistsDeviceCodeMethod()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "EntraId",
            EntraTenantId = "tenant-id",
            EntraClientId = "client-id",
            EntraTokenScope = "api://backend/.default",
            EntraSignInMethod = "InteractiveBrowser",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await cloudCredentialStore.Received(1).SaveConfigAsync(Arg.Is<StoredCloudProviderConfig>(config =>
                config.AzureFoundry!.EntraClientSecret == null
                && config.AzureFoundry.EntraSignInMethod == EntraSignInMethod.InteractiveBrowser),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenEntraIdSecretBlankOnExistingEntraIdConnection_KeepsStoredSecret()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateEntraIdConfig());
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "EntraId",
            EntraTenantId = "tenant-id",
            EntraClientId = "client-id",
            EntraClientSecret = null,
            EntraTokenScope = "api://backend/.default",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(body.Contains("entra-client-secret-value", StringComparison.Ordinal));
        await cloudCredentialStore.Received(1).SaveConfigAsync(Arg.Is<StoredCloudProviderConfig>(config =>
                config.AzureFoundry!.EntraClientSecret == "entra-client-secret-value"
                && config.AzureFoundry.EntraSignInMethod == EntraSignInMethod.ClientSecret),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenEntraIdSecretBlankAndSwitchingFromApiKeyMode_DoesNotResurrectSecret()
    {
        // The prior connection was ApiKey-mode (no Entra secret ever stored); switching to EntraId with a blank
        // secret must never inherit anything, and must fall back to interactive sign-in.
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        cloudCredentialStore.LoadConfigAsync(Arg.Any<CancellationToken>()).Returns(CreateConfig());
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "EntraId",
            EntraTenantId = "tenant-id",
            EntraClientId = "client-id",
            EntraClientSecret = null,
            EntraTokenScope = "api://backend/.default",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await cloudCredentialStore.Received(1).SaveConfigAsync(Arg.Is<StoredCloudProviderConfig>(config =>
                config.AzureFoundry!.EntraClientSecret == null
                && config.AzureFoundry.EntraSignInMethod == EntraSignInMethod.DeviceCode),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenAuthModeIsUnrecognized_ReturnsValidationProblem()
    {
        // The request-level validator rejects an unparseable AuthMode outright (before the endpoint ever runs), so
        // CloudSettingsEndpointDtoMapper.ParseAuthMode's ApiKey fallback is unreachable via this route in practice —
        // it exists only as a defensive default for direct mapper use.
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "NotARealAuthMode",
            ApiKey = "some-api-key",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await cloudCredentialStore.DidNotReceiveWithAnyArgs().SaveConfigAsync(Arg.Any<StoredCloudProviderConfig>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenApiSurfaceIsOpenAiV1_PersistsAndReturnsIt()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "ApiKey",
            ApiSurface = "OpenAiV1",
            ApiKey = "new-secret-api-key",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var settings = Deserialize<CloudSettingsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("OpenAiV1", settings.AzureFoundry!.ApiSurface);
        await cloudCredentialStore.Received(1).SaveConfigAsync(
            Arg.Is<StoredCloudProviderConfig>(config => config.AzureFoundry!.ApiSurface == AzureFoundryApiSurface.OpenAiV1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenApiSurfaceIsUnrecognized_ReturnsValidationProblem()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "ApiKey",
            ApiSurface = "NotARealApiSurface",
            ApiKey = "some-api-key",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await cloudCredentialStore.DidNotReceiveWithAnyArgs().SaveConfigAsync(Arg.Any<StoredCloudProviderConfig>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SaveCloudSettings_WhenEntraIdMissingTenantId_ReturnsValidationProblem()
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "EntraId",
            EntraClientId = "client-id",
            EntraTokenScope = "api://backend/.default",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await cloudCredentialStore.DidNotReceiveWithAnyArgs().SaveConfigAsync(Arg.Any<StoredCloudProviderConfig>(), Arg.Any<CancellationToken>());
    }

    private static StoredCloudProviderConfig CreateEntraIdConfig()
    {
        return new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = "https://example.openai.azure.com/",
                AuthMode = AzureFoundryAuthMode.EntraId,
                EntraTenantId = "tenant-id",
                EntraClientId = "client-id",
                EntraClientSecret = "entra-client-secret-value",
                EntraTokenScope = "api://backend/.default",
                EntraSignInMethod = EntraSignInMethod.ClientSecret,
                Models =
                [
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = "gpt-4o",
                        DisplayLabel = "gpt-4o"
                    }
                ]
            }
        };
    }

    [Test]
    public async Task SaveCloudSettings_WhenFreshSecretHeaderIsBlank_ReturnsValidationProblem()
    {
        // No stored config at all: a brand-new secret header sent with a blank value has nothing to merge against and
        // must be rejected as a 400 (previously threw ArgumentException in CloudCredentialStore.ValidateConfig -> 500).
        await AssertSaveRejectedAsync(new SaveAzureFoundryHeaderRequest
        {
            Name = "X-Api-Token",
            Value = null,
            IsSecret = true
        });
    }

    private static async Task AssertSaveRejectedAsync(params SaveAzureFoundryHeaderRequest[] headers)
    {
        var cloudCredentialStore = Substitute.For<ICloudCredentialStore>();
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(CreateSaveRequest(headers));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await cloudCredentialStore.DidNotReceiveWithAnyArgs().SaveConfigAsync(Arg.Any<StoredCloudProviderConfig>(), Arg.Any<CancellationToken>());
    }

    private static async Task<string> SendSaveAsync(ICloudCredentialStore cloudCredentialStore,
        ICapabilityReporter capabilityReporter,
        params SaveAzureFoundryHeaderRequest[] headers)
    {
        await using var factory = CreateFactory(cloudCredentialStore, capabilityReporter);
        using var client = factory.CreateClient();

        using var request = CreateRequest(factory, HttpMethod.Put, "/api/local/v1/cloud-settings");
        request.Content = JsonContent.Create(CreateSaveRequest(headers));
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static SaveCloudSettingsRequest CreateSaveRequest(IReadOnlyList<SaveAzureFoundryHeaderRequest> headers)
    {
        return new SaveCloudSettingsRequest
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = "ApiKey",
            ApiKey = "new-secret-api-key",
            Models =
            [
                new AzureFoundryModelDto
                {
                    DeploymentName = "gpt-4o"
                }
            ],
            Headers = headers
        };
    }

    private static StoredCloudProviderConfig CreateConfigWithHeaders(params StoredAzureFoundryHeader[] headers)
    {
        return new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = "https://example.openai.azure.com/",
                AuthMode = AzureFoundryAuthMode.ApiKey,
                ApiKey = "test-api-key",
                Models =
                [
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = "gpt-4o",
                        DisplayLabel = "gpt-4o"
                    }
                ],
                Headers = headers
            }
        };
    }

    private static TestingWebAppFactory CreateFactory(ICloudCredentialStore cloudCredentialStore, ICapabilityReporter? capabilityReporter = null)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ICloudCredentialStore>();
                services.AddSingleton(cloudCredentialStore);
                services.RemoveAll<ICapabilityReporter>();
                services.AddSingleton(capabilityReporter ?? Substitute.For<ICapabilityReporter>());
            }
        };
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static StoredCloudProviderConfig CreateConfig()
    {
        return new StoredCloudProviderConfig
        {
            ProviderName = "AzureFoundry",
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = "https://example.openai.azure.com/",
                AuthMode = AzureFoundryAuthMode.ApiKey,
                ApiKey = "test-api-key",
                Models =
                [
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = "gpt-4o",
                        DisplayLabel = "gpt-4o"
                    }
                ]
            }
        };
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(json, JsonOptions));
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}
