namespace XE_Local_AI_Engine.Tests.Coder;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Coder;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Client.Services.Coder.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Handler-shape coverage for the three coder tools: each flag-gates on <c>AgentHome:Enabled</c>, validates before
///     touching the reader (reject-before-side-effect), surfaces validation errors, and reports
///     <see cref="IClientLocalToolHandler.RequiresApproval" /> == false.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CoderToolHandlerTests
{
    [Test]
    public async Task ListFiles_WhenAgentHomeDisabled_ReturnsDisabledMessage()
    {
        var reader = new RecordingReader();
        var handler = new ListFilesToolHandler(Settings(enabled: false), reader);

        var result = await handler.ExecuteAsync("{}");

        AssertEx.Contains(result, "disabled", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(reader.WasCalled, "a disabled node must reject before the reader");
    }

    [Test]
    public async Task ReadFile_WhenAgentHomeDisabled_ReturnsDisabledMessage()
    {
        var reader = new RecordingReader();
        var handler = new ReadFileToolHandler(Settings(enabled: false), reader);

        var result = await handler.ExecuteAsync("""{"path":"src/a.txt"}""");

        AssertEx.Contains(result, "disabled", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(reader.WasCalled, "a disabled node must reject before the reader");
    }

    [Test]
    public async Task SearchText_WhenAgentHomeDisabled_ReturnsDisabledMessage()
    {
        var reader = new RecordingReader();
        var handler = new SearchTextToolHandler(Settings(enabled: false), reader);

        var result = await handler.ExecuteAsync("""{"pattern":"x"}""");

        AssertEx.Contains(result, "disabled", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(reader.WasCalled, "a disabled node must reject before the reader");
    }

    [Test]
    public async Task ReadFile_WhenPathMissing_ReturnsValidationError()
    {
        var reader = new RecordingReader();
        var handler = new ReadFileToolHandler(Settings(enabled: true), reader);

        var result = await handler.ExecuteAsync("{}");

        AssertEx.Contains(result, "invalid", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "path");
        AssertEx.False(reader.WasCalled, "invalid arguments must reject before the reader");
    }

    [Test]
    public async Task SearchText_WhenInvalidRegex_ReturnsValidationError()
    {
        var reader = new RecordingReader();
        var handler = new SearchTextToolHandler(Settings(enabled: true), reader);

        var result = await handler.ExecuteAsync("""{"pattern":"a(b","isRegex":true}""");

        AssertEx.Contains(result, "invalid", StringComparison.OrdinalIgnoreCase);
        AssertEx.False(reader.WasCalled, "a bad regex must reject before the reader");
    }

    [Test]
    public async Task Handlers_WhenEnabledAndValid_DelegateToReader()
    {
        var reader = new RecordingReader();
        var list = new ListFilesToolHandler(Settings(enabled: true), reader);
        var read = new ReadFileToolHandler(Settings(enabled: true), reader);
        var search = new SearchTextToolHandler(Settings(enabled: true), reader);

        AssertEx.Equal("list-ok", await list.ExecuteAsync("{}"));
        AssertEx.Equal("read-ok", await read.ExecuteAsync("""{"path":"src/a.txt"}"""));
        AssertEx.Equal("search-ok", await search.ExecuteAsync("""{"pattern":"x"}"""));
    }

    [Test]
    public async Task Handlers_ReadTheStoredSwitchPerCall_OverTheConfigurationSeed()
    {
        var stored = new StoredNodeSettings
        {
            AgentHomeEnabled = true
        };
        var reader = new RecordingReader();
        var list = new ListFilesToolHandler(SeededNodeRuntimeSettings.Create(new Dictionary<string, string?>
        {
            ["AgentHome:Enabled"] = "false"
        }, () => stored), reader);

        AssertEx.Equal("list-ok", await list.ExecuteAsync("{}"), "a stored on must win over the seed's off");

        stored = new StoredNodeSettings
        {
            AgentHomeEnabled = false
        };
        AssertEx.Contains(await list.ExecuteAsync("{}"), "disabled", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public void CoderHandlers_DoNotRequireApproval()
    {
        var reader = new RecordingReader();
        IClientLocalToolHandler list = new ListFilesToolHandler(Settings(enabled: true), reader);
        IClientLocalToolHandler read = new ReadFileToolHandler(Settings(enabled: true), reader);
        IClientLocalToolHandler search = new SearchTextToolHandler(Settings(enabled: true), reader);

        AssertEx.False(list.RequiresApproval, "list_files is read-only and auto-runs");
        AssertEx.False(read.RequiresApproval, "read_file is read-only and auto-runs");
        AssertEx.False(search.RequiresApproval, "search_text is read-only and auto-runs");
    }

    private static INodeRuntimeSettings Settings(bool enabled) =>
        SeededNodeRuntimeSettings.FromSeed("AgentHome:Enabled", enabled);

    private sealed class RecordingReader : ICoderWorkspaceReader
    {
        public bool WasCalled { get; private set; }

        public Task<string> ListFilesAsync(ListFilesToolRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult("list-ok");
        }

        public Task<string> ReadFileAsync(ReadFileToolRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult("read-ok");
        }

        public Task<string> SearchTextAsync(SearchTextToolRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult("search-ok");
        }
    }
}
