namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Ruling D6's gate where a definition meets it: a <c>Tool</c> node may name only a tool
///     <c>IToolInvocationService</c> would actually invoke, checked at SAVE and again at RUN START.
///     <para>
///         The catalog is the real one — the envelope is the thing under test, so faking it here would prove only that
///         a fake answers. The one substituted catalog below is the case a real one cannot stage: a definition saved
///         while its tool was invocable, started after the envelope tightened away from it.
///     </para>
/// </summary>
public sealed class GraphWorkflowToolValidationTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     <c>run_python</c> parses like any other tool name, so nothing structural refuses it: only the catalog knows
    ///     it is WriteExecute, and the refusal has to arrive keyed to the node that named it.
    /// </summary>
    [Test]
    public async Task CreateAsync_WithAToolOutsideTheEnvelope_RefusesKeyedByTheNodeAndNamesTheTool()
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
                                        definitions.CreateAsync($"Refused {Guid.NewGuid():N}",
                                            description: null,
                                            GraphWorkflowGraphs.ToolValidationWriteExecuteTool))
                                    .ConfigureAwait(false);

        var error = AssertEx.NotNull(refusal.Result.Errors.SingleOrDefault(), $"one offending node, one error: {refusal.Message}");
        AssertEx.Equal("runner", error.Key, "the error is keyed by NODE key, so the editor draws it on the node that named the tool.");
        AssertEx.Contains(error.Message, "run_python", message: "and names the tool, so the author knows which one to replace.");
    }

    /// <summary>
    ///     Two refusals for two reasons — a write tool and an approval-gated one — because a gate that stopped at the
    ///     first would send an author round the loop once per bad node.
    /// </summary>
    [Test]
    public async Task CreateAsync_WithTwoRefusedToolNodes_ReportsBothKeys()
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
                                        definitions.CreateAsync($"Refused {Guid.NewGuid():N}",
                                            description: null,
                                            GraphWorkflowGraphs.ToolValidationTwoRefusedTools))
                                    .ConfigureAwait(false);

        AssertEx.Equal("asker,runner",
            string.Join(',', refusal.Result.Errors.Select(static error => error.Key).Order(StringComparer.Ordinal)),
            $"both offending nodes, not the first one: {refusal.Message}");
        AssertEx.Contains(refusal.Result.Errors, static error => error.Message.Contains("ask_user", StringComparison.Ordinal));
        AssertEx.Contains(refusal.Result.Errors, static error => error.Message.Contains("run_python", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The editor's probe answers the same complaint the save would have thrown, and leaves nothing behind — which
    ///     is what an author asking about a half-written canvas is owed.
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithARefusedTool_ReportsTheSameErrorAndSavesNothing()
    {
        // A host of this test's own: "no definition was written" is an absolute row count, and a concurrent sibling
        // seeding its own definition on the shared host would make it unanswerable.
        await using var factory = GraphWorkflowHostFixture.NewFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        var before = (await store.ListDefinitionsAsync().ConfigureAwait(false)).Count;

        var result = await definitions.ValidateAsync(GraphWorkflowGraphs.ToolValidationWriteExecuteTool).ConfigureAwait(false);

        AssertEx.False(result.IsValid, "the probe never throws, so the refusal has to arrive as a report.");
        var error = AssertEx.NotNull(result.Errors.SingleOrDefault(), "the same single error the save throws.");
        AssertEx.Equal("runner", error.Key);
        AssertEx.Contains(error.Message, "run_python");
        AssertEx.Equal(before, (await store.ListDefinitionsAsync().ConfigureAwait(false)).Count, "a probe writes no definition row.");
    }

    /// <summary>
    ///     The whole reason the check runs twice. The definition is saved while its tools are invocable and started
    ///     after the envelope tightened away from them: run start wins, and it wins BEFORE the run row is written, so
    ///     the operator learns at the start rather than three nodes in.
    /// </summary>
    [Test]
    public async Task StartAsync_WhenTheEnvelopeTightenedAfterTheSave_RefusesTheStartAndWritesNoRun()
    {
        var tools = Substitute.For<IToolInvocationService>();
        IReadOnlyList<InvocableToolDescriptor> catalog =
        [
            new InvocableToolDescriptor("read_file", "Reads a file.", """{"type":"object"}"""),
            new InvocableToolDescriptor("list_files", "Lists files.", """{"type":"object"}""")
        ];
        _ = tools.ListInvocableToolsAsync(Arg.Any<CancellationToken>()).Returns(catalog);

        // A host of this test's own: the catalog is host-level state, and a sibling saving a Tool graph on the shared
        // host must not see it emptied underneath.
        await using var factory = GraphWorkflowHostFixture.NewFactory(services =>
        {
            services.RemoveAll<IToolInvocationService>();
            services.AddSingleton(tools);
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>()
                                    .CreateAsync($"Tightened {Guid.NewGuid():N}", description: null, GraphWorkflowGraphs.ToolNode)
                                    .ConfigureAwait(false);

        // The tightening itself: the same catalog, now offering neither tool.
        _ = tools.ListInvocableToolsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var requestId = Guid.NewGuid();

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
                                        scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>()
                                             .StartAsync(definition.Id, requestId, inputJson: null, definitionVersion: null))
                                    .ConfigureAwait(false);

        AssertEx.Equal("lookup,peek",
            string.Join(',', refusal.Result.Errors.Select(static error => error.Key).Order(StringComparer.Ordinal)),
            $"the start reports every Tool node it can no longer run, keyed as the save's would be: {refusal.Message}");
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        AssertEx.Null(await store.FindRunByRequestAsync(requestId).ConfigureAwait(false), "the refusal lands before the run row, so nothing is left half-started.");
    }
}
