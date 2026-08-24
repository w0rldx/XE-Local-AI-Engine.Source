namespace XE_Local_AI_Engine.Tests.Compute;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Handler-level behavior for <c>run_python</c>, at parity with <c>RunInAgentHomeToolHandlerTests</c>: the node
///     kill-switch short-circuits before the gateway, arguments are validated before anything executes, and
///     cancellation propagates as cancellation.
/// </summary>
public sealed class RunPythonToolHandlerTests
{
    private const string ValidArguments = """{"code":"print(2 + 2)"}""";

    [Test]
    public async Task ExecuteAsync_WhenComputeDisabled_ReturnsDisabledMessage()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: false, gateway);

        var result = await handler.ExecuteAsync(ValidArguments);

        AssertEx.Contains(result, "disabled", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(gateway.WasCalled, "a disabled node must reject before the gateway, so no venv or sandbox work happens");
    }

    [Test]
    public async Task ExecuteAsync_WhenEnabledAndValid_DelegatesToGateway()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: true, gateway);

        var result = await handler.ExecuteAsync(ValidArguments);

        AssertEx.Equal("run reached the gateway", result);
        AssertEx.Equal("print(2 + 2)", gateway.LastRequest?.Code);
    }

    [Test]
    public async Task ExecuteAsync_WhenCodeIsMissing_ReturnsValidationErrors()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: true, gateway);

        var result = await handler.ExecuteAsync("""{"code":"   "}""");

        AssertEx.Contains(result, "invalid", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "code");
        AssertEx.False(gateway.WasCalled, "invalid arguments must reject before the gateway");
    }

    [Test]
    public async Task ExecuteAsync_WhenCodeExceedsTheCeiling_ReturnsValidationErrors()
    {
        // The schema carries no maxLength (it would be stripped from the llama.cpp wire anyway), so this handler-side
        // ceiling is the ONLY thing bounding a submitted script. If it stops being enforced nothing else catches it.
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: true, gateway);
        var oversized = new string('x', ComputeToolDefinition.CodeMaxLength + 1);

        var result = await handler.ExecuteAsync($$"""{"code":"{{oversized}}"}""");

        AssertEx.Contains(result, "invalid", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(gateway.WasCalled, "an oversized script must reject before the gateway");
    }

    [Test]
    public async Task ExecuteAsync_WhenArgumentsAreNotJson_ReturnsAParseMessage()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = CreateHandler(enabled: true, gateway);

        var result = await handler.ExecuteAsync("{not json");

        AssertEx.Contains(result, "valid JSON", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(gateway.WasCalled);
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

    [Test]
    public void Handler_RequiresApproval_AndAdvertisesTheSharedDefinition()
    {
        var handler = CreateHandler(enabled: true, new StubGateway("unused"));

        AssertEx.True(handler.RequiresApproval, "executing model-authored code must stay approval-gated");
        AssertEx.Equal(ComputeToolDefinition.ToolName, handler.ToolName);
        AssertEx.Equal(ComputeToolDefinition.ParameterSchema, handler.ParameterSchema);
    }

    private static RunPythonToolHandler CreateHandler(bool enabled, IComputeToolGateway gateway)
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Compute:Enabled"] = enabled ? "true" : "false"
                            })
                            .Build();

        return new RunPythonToolHandler(configuration, gateway);
    }

    private sealed class StubGateway : IComputeToolGateway
    {
        private readonly string _result;

        public StubGateway(string result)
        {
            _result = result;
        }

        public bool WasCalled { get; private set; }

        public ComputeRunToolRequest? LastRequest { get; private set; }

        public Task<string> ExecuteAsync(ComputeRunToolRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }
}
