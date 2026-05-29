namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RunInAgentHomeToolHandlerTests
{
    private const string ValidArguments =
        """{"goal":"analyze the project","selectedFolderIds":["folder-123"],"allowedActions":["read_workspace"]}""";

    [Test]
    public async Task ExecuteAsync_WhenAgentHomeDisabled_ReturnsDisabledMessage()
    {
        var handler = CreateHandler(enabled: false);

        var result = await handler.ExecuteAsync(ValidArguments);

        AssertEx.Contains(result, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task ExecuteAsync_WhenEnabledAndValid_DelegatesToPendingGateway()
    {
        var handler = CreateHandler(enabled: true);

        var result = await handler.ExecuteAsync(ValidArguments);

        AssertEx.Contains(result, "not yet available", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task ExecuteAsync_WhenEnabledAndInvalid_ReturnsValidationErrors()
    {
        var handler = CreateHandler(enabled: true);

        var result = await handler.ExecuteAsync("""{"selectedFolderIds":[],"allowedActions":[]}""");

        AssertEx.Contains(result, "invalid", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "goal");
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_Throws()
    {
        var handler = CreateHandler(enabled: true);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var threw = false;
        try
        {
            await handler.ExecuteAsync(ValidArguments, cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        AssertEx.True(threw);
    }

    private static RunInAgentHomeToolHandler CreateHandler(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentHome:Enabled"] = enabled ? "true" : "false"
            })
            .Build();

        return new RunInAgentHomeToolHandler(configuration, new PendingAgentHomeToolGateway());
    }
}
