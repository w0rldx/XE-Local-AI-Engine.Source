namespace XE_Local_AI_Engine.Tests.Integration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class ApplicationStartupTests
{
    [Test]
    public async Task Application_StartsWithoutException()
    {
        await using var factory = new TestServerWebAppFactory();
        AssertEx.NotNull(factory.Services);
    }

    [Test]
    public async Task IWorkerHubConnection_IsRegistered()
    {
        await using var factory = new TestServerWebAppFactory();
        AssertEx.NotNull(factory.Services.GetRequiredService<IWorkerHubConnection>());
    }

    [Test]
    public async Task IPairingService_IsRegistered()
    {
        await using var factory = new TestServerWebAppFactory();
        AssertEx.NotNull(factory.Services.GetRequiredService<IPairingService>());
    }

    [Test]
    public async Task ITokenStore_IsRegistered()
    {
        await using var factory = new TestServerWebAppFactory();
        var tokenStore = factory.Services.GetRequiredService<ITokenStore>();

        AssertEx.NotNull(tokenStore);
        AssertEx.True(tokenStore is MockTokenStore);
    }

    [Test]
    public async Task IInvocationRunner_IsRegistered()
    {
        await using var factory = new TestServerWebAppFactory();
        AssertEx.NotNull(factory.Services.GetRequiredService<IInvocationRunner>());
    }

    [Test]
    public async Task IDeadLetterStore_IsRegistered()
    {
        await using var factory = new TestServerWebAppFactory();
        AssertEx.NotNull(factory.Services.GetRequiredService<IDeadLetterStore>());
    }

    [Test]
    public async Task ICapabilityReporter_IsRegistered()
    {
        await using var factory = new TestServerWebAppFactory();
        AssertEx.NotNull(factory.Services.GetRequiredService<ICapabilityReporter>());
    }

    [Test]
    public async Task IWorkerShutdownDrainService_IsRegistered()
    {
        await using var factory = new TestServerWebAppFactory();

        AssertEx.NotNull(factory.Services.GetRequiredService<IWorkerShutdownDrainService>());
        AssertEx.Equal(WorkerShutdownDrainOptions.DefaultDrainTimeout,
            factory.Services.GetRequiredService<IOptions<WorkerShutdownDrainOptions>>().Value.DrainTimeout);
    }

    [Test]
    public async Task ConfigurationValidation_WithMissingBaseUrl_FailsStartup()
    {
        await using var invalidFactory = new TestServerWebAppFactory
        {
            SkipDefaultBaseUrlOverride = true,
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["CentralPlatform:BaseUrl"] = string.Empty
            }
        };

        Exception? exception = null;

        try
        {
            _ = invalidFactory.Services;
            throw new AssertionException("Expected startup to fail for missing CentralPlatform:BaseUrl.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OptionsValidationException)
        {
            exception = ex;
        }

        AssertEx.NotNull(exception);
    }
}
