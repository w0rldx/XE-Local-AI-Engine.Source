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
    /// <summary>
    ///     One host for the whole class. Every test below only resolves services from the built host — nothing mutates
    ///     host state — so a single bootstrap answers all of them. The missing-base-url test deliberately keeps its own
    ///     host: it asserts that startup FAILS, which a shared host cannot express.
    /// </summary>
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Application_StartsWithoutException()
    {
        var factory = Factory;
        AssertEx.NotNull(factory.Services);
    }

    [Test]
    public async Task IWorkerHubConnection_IsRegistered()
    {
        var factory = Factory;
        AssertEx.NotNull(factory.Services.GetRequiredService<IWorkerHubConnection>());
    }

    [Test]
    public async Task IPairingService_IsRegistered()
    {
        var factory = Factory;
        AssertEx.NotNull(factory.Services.GetRequiredService<IPairingService>());
    }

    [Test]
    public async Task ITokenStore_IsRegistered()
    {
        var factory = Factory;
        var tokenStore = factory.Services.GetRequiredService<ITokenStore>();

        AssertEx.NotNull(tokenStore);
        AssertEx.True(tokenStore is MockTokenStore);
    }

    [Test]
    public async Task IInvocationRunner_IsRegistered()
    {
        var factory = Factory;
        AssertEx.NotNull(factory.Services.GetRequiredService<IInvocationRunner>());
    }

    [Test]
    public async Task IDeadLetterStore_IsRegistered()
    {
        var factory = Factory;
        AssertEx.NotNull(factory.Services.GetRequiredService<IDeadLetterStore>());
    }

    [Test]
    public async Task ICapabilityReporter_IsRegistered()
    {
        var factory = Factory;
        AssertEx.NotNull(factory.Services.GetRequiredService<ICapabilityReporter>());
    }

    [Test]
    public async Task IWorkerShutdownDrainService_IsRegistered()
    {
        var factory = Factory;

        AssertEx.NotNull(factory.Services.GetRequiredService<IWorkerShutdownDrainService>());
        AssertEx.Equal(WorkerShutdownDrainOptions.DefaultDrainTimeout,
            factory.Services.GetRequiredService<IOptions<WorkerShutdownDrainOptions>>().Value.DrainTimeout);
    }

    [Test]
    public async Task ConfigurationValidation_WithAParkBudgetOverTheNodeToolCallAge_FailsStartup()
    {
        // The relation neither section's data annotations can see: 600 seconds of park against the node's 10-minute
        // pending tool-call age. A node that boots with this pair parks on calls the node has already expired.
        await using var invalidFactory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["WorkSessions:MaxParkedSeconds"] = "600",
                ["WorkerNode:MaxPendingToolCallAgeMinutes"] = "10"
            }
        };

        Exception? exception = null;

        try
        {
            _ = invalidFactory.Services;
            throw new AssertionException("Expected startup to fail for a park budget at or over the node's pending tool-call age.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OptionsValidationException)
        {
            exception = ex;
        }

        AssertEx.NotNull(exception);
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
