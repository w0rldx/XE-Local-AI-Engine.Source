namespace XE_Local_AI_Engine.Tests.Compute;

using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Handler-level behavior for <c>run_python</c>: the JSON envelope is the handler's own responsibility, everything
///     the request then has to satisfy is the gateway's, and cancellation propagates as cancellation.
///     <para>
///         The kill-switch and the request validation used to be asserted here. They now live in
///         <see cref="IComputeToolGateway.ExecuteDetailedAsync" /> and are asserted in
///         <see cref="ComputeToolGatewayTests" /> — deliberately, because a check this handler owned was a check no
///         other caller of the gateway got.
///     </para>
/// </summary>
public sealed class RunPythonToolHandlerTests
{
    private const string ValidArguments = """{"code":"print(2 + 2)"}""";

    [Test]
    public async Task ExecuteAsync_WhenTheEnvelopeParses_DelegatesToTheGateway()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = new RunPythonToolHandler(gateway);

        var result = await handler.ExecuteAsync(ValidArguments);

        AssertEx.Equal("run reached the gateway", result);
        AssertEx.Equal("print(2 + 2)", gateway.LastRequest?.Code);
    }

    [Test]
    public async Task ExecuteAsync_DelegatesWithoutPreScreening_SoTheGatewayOwnsEveryRefusal()
    {
        // An empty script and an oversized one both reach the gateway now. That IS the contract: the handler holding a
        // copy of the validation is what let a second caller of the gateway run unvalidated code.
        var gateway = new StubGateway("run_python arguments are invalid: 'code' is required and must be a non-empty string.");
        var handler = new RunPythonToolHandler(gateway);

        var result = await handler.ExecuteAsync("""{"code":"   "}""");

        AssertEx.True(gateway.WasCalled, "the gateway is the only place the request is judged");
        AssertEx.Contains(result, "invalid", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task ExecuteAsync_WhenArgumentsAreNotJson_ReturnsAParseMessage()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = new RunPythonToolHandler(gateway);

        var result = await handler.ExecuteAsync("{not json");

        AssertEx.Contains(result, "valid JSON", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(gateway.WasCalled, "there is no request to hand on");
    }

    [Test]
    public async Task ExecuteAsync_WhenArgumentsAreNull_ReturnsAnEmptyMessage()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = new RunPythonToolHandler(gateway);

        var result = await handler.ExecuteAsync("null");

        AssertEx.Contains(result, "empty", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(gateway.WasCalled);
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_Throws()
    {
        var gateway = new StubGateway("run reached the gateway");
        var handler = new RunPythonToolHandler(gateway);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            handler.ExecuteAsync(ValidArguments, cancellationTokenSource.Token));
    }

    [Test]
    public void Handler_RequiresApproval_AndAdvertisesTheSharedDefinition()
    {
        var handler = new RunPythonToolHandler(new StubGateway("unused"));

        AssertEx.True(handler.RequiresApproval, "executing model-authored code must stay approval-gated");
        AssertEx.Equal(ComputeToolDefinition.ToolName, handler.ToolName);
        AssertEx.Equal(ComputeToolDefinition.ParameterSchema, handler.ParameterSchema);
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

        public Task<ComputeExecutionOutcome> ExecuteDetailedAsync(ComputeRunToolRequest request,
            bool requireResourceLimits,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("the handler renders through ExecuteAsync");
        }
    }
}
