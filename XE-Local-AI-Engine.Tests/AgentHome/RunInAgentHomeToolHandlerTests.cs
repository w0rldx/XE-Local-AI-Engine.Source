namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RunInAgentHomeToolHandlerTests
{
    private const string ValidArguments =
        """{"goal":"analyze the project","selectedFolderIds":["3f2504e0-4f89-41d3-9a0c-0305e82c3301"],"allowedActions":["read_workspace"]}""";

    [Test]
    public async Task ExecuteAsync_WhenAgentHomeDisabled_ReturnsDisabledMessage()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: false, gateway);

        var result = await handler.ExecuteAsync(ValidArguments);

        AssertEx.Contains(result, "disabled", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(gateway.WasCalled, "a disabled node must reject before the gateway");
    }

    [Test]
    public async Task ExecuteAsync_WhenEnabledAndValid_DelegatesToGateway()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: true, gateway);

        var result = await handler.ExecuteAsync(ValidArguments);

        AssertEx.Equal("run reached the gateway", result);
        AssertEx.True(gateway.WasCalled);
    }

    [Test]
    public async Task ExecuteAsync_WhenEnabledAndInvalid_ReturnsValidationErrors()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: true, gateway);

        var result = await handler.ExecuteAsync("""{"selectedFolderIds":[],"allowedActions":[]}""");

        AssertEx.Contains(result, "invalid", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "goal");
        AssertEx.False(gateway.WasCalled, "invalid arguments must reject before the gateway");
    }

    [Test]
    public async Task ExecuteAsync_WhenSelectedFolderIdIsRawHostPath_ReturnsValidationErrors()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: true, gateway);

        var result = await handler.ExecuteAsync(
            """{"goal":"g","selectedFolderIds":["/etc/passwd"],"allowedActions":["read_workspace"]}""");

        AssertEx.Contains(result, "invalid", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(gateway.WasCalled, "a raw host path id must reject before the gateway");
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_Throws()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: true, gateway);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(ValidArguments, cancellationTokenSource.Token));
    }

    private static RunInAgentHomeToolHandler CreateHandler(bool enabled, IAgentHomeToolGateway gateway)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentHome:Enabled"] = enabled ? "true" : "false"
            })
            .Build();

        return new RunInAgentHomeToolHandler(configuration, gateway);
    }

    private sealed class StubGateway : IAgentHomeToolGateway
    {
        private readonly string _result;

        public StubGateway(string result)
        {
            _result = result;
        }

        public bool WasCalled { get; private set; }

        public Task<string> ExecuteAsync(AgentHomeRunToolRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(_result);
        }
    }
}
