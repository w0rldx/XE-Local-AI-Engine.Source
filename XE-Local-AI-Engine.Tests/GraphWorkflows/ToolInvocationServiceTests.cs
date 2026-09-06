namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The D6 envelope, asserted where it is enforced. <c>IToolInvocationService</c> is the ONLY place a workflow node's
///     tool call is admitted, so this class pins both halves of that claim: which tools get through (exactly the eight
///     built-in read tools), and that every refusal — risk class, composed approval, unknown name, bad arguments, a
///     spent budget — comes back as an outcome rather than an exception or a silent success.
///     <para>
///         The service is feature-neutral and registered unconditionally, so the plain host is the right one: no
///         Graph Workflows flag is set anywhere in this class.
///     </para>
///     <para>
///         The MCP half of "closed by construction" is pinned at ITS definition site by
///         <c>McpServerConnectionManagerTests</c>, which drives a real in-process server and asserts WriteExecute for a
///         privileged stdio record, Network for every other, approval forced on, and the structural approval wrap.
///         Re-staging a server here would duplicate that without adding a gate; the custom-tool half has no such
///         existing pin, so it is asserted below against the real store and the real catalog.
///     </para>
/// </summary>
public sealed class ToolInvocationServiceTests
{
    /// <summary>The eight tools that pass BOTH gates at this tip. A category change anywhere moves this set.</summary>
    private static readonly string[] ExpectedInvocable =
    [
        "Calculate",
        "GetCurrentTime",
        "list_files",
        "read_document",
        "read_file",
        "read_surrounding_chunks",
        "search_knowledge_base",
        "search_text"
    ];

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    private static ToolInvocationContext Context(TimeSpan? timeout = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "tool-node", timeout ?? TimeSpan.FromSeconds(30));

    private static IToolInvocationService ServiceOf(TestServerWebAppFactory factory) =>
        factory.Services.GetRequiredService<IToolInvocationService>();

    [Test]
    public async Task Invoke_TheTimeTool_RunsInProcessAndReturnsItsOwnText()
    {
        // GetCurrentTime is the end-to-end tool: no feature flag, no workspace, no model behind it.
        var outcome = await ServiceOf(Factory).InvokeAsync("GetCurrentTime", "{}", Context()).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.Executed, outcome.Kind, outcome.Reason);
        AssertEx.NotNullOrEmpty(outcome.Result);
        AssertEx.Contains(outcome.Result, "UTC time:", message: "the tool's own text must arrive unwrapped, not as a quoted JSON literal.");
    }

    [Test]
    public async Task Invoke_TheCalculatorTool_PassesItsArgumentsThrough()
    {
        var outcome = await ServiceOf(Factory).InvokeAsync("Calculate", """{"expression":"2+2"}""", Context()).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.Executed, outcome.Kind, outcome.Reason);
        AssertEx.Contains(outcome.Result, "4");
    }

    [Test]
    public async Task ListInvocableTools_IsExactlyTheEightBuiltInReadTools_EachWithASchema()
    {
        // The set IS the D6 envelope, so it is asserted as a set rather than a spot-check: a future category or
        // approval change on any node tool moves this list and fails here rather than silently widening a Tool node.
        var invocable = await ServiceOf(Factory).ListInvocableToolsAsync().ConfigureAwait(false);

        AssertEx.Equal(string.Join(',', ExpectedInvocable),
            string.Join(',', invocable.Select(static tool => tool.Name).Order(StringComparer.Ordinal)));
        foreach (var tool in invocable)
        {
            AssertEx.NotNullOrEmpty(tool.ParameterSchema, $"{tool.Name} must carry the schema the service validates against.");
            AssertEx.NotNullOrEmpty(tool.Description, $"{tool.Name} must carry a description for the picker.");
        }
    }

    /// <summary>
    ///     <c>read_file</c> is listed because it is INVOCABLE, which is not the same as useful: its handler answers
    ///     "Agent Mode is disabled" when the feature is off, and that is a successful invocation of a gated tool.
    /// </summary>
    [Test]
    public async Task ListInvocableTools_IncludesReadFile_WhoseExecutableComesFromTheWorkerRegistry()
    {
        var invocable = await ServiceOf(Factory).ListInvocableToolsAsync().ConfigureAwait(false);

        var readFile = AssertEx.NotNull(invocable.SingleOrDefault(static tool => tool.Name == "read_file"),
            "read_file resolves through IClientLocalToolRegistry, which the promoted precedent never consulted.");
        AssertEx.Contains(readFile.ParameterSchema, "path", message: "the schema must be the handler's own, not an empty object.");
    }

    [Test]
    [Arguments("run_python", "not-read-local")]
    [Arguments("spawn_subagent", "not-read-local")]
    [Arguments("update_work_plan", "not-read-local")]
    [Arguments("ask_user", "approval-gated")]
    public async Task Invoke_AToolOutsideTheEnvelope_IsRefusedWithItsGatesReason(string toolName, string expectedReason)
    {
        var service = ServiceOf(Factory);

        var outcome = await service.InvokeAsync(toolName, "{}", Context()).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.NotInvocable, outcome.Kind);
        AssertEx.Equal(expectedReason, outcome.Reason);
        AssertEx.False((await service.ListInvocableToolsAsync().ConfigureAwait(false)).Any(tool => tool.Name == toolName),
            $"{toolName} must not be offered to a picker it can never run from.");
    }

    [Test]
    [Arguments("no_such_tool")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Invoke_ANameTheCatalogDoesNotCarry_IsUnknownTool(string toolName)
    {
        var outcome = await ServiceOf(Factory).InvokeAsync(toolName, "{}", Context()).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.UnknownTool, outcome.Kind);
        AssertEx.NotNullOrEmpty(outcome.Reason);
    }

    [Test]
    [Arguments("{}", "a required property is missing")]
    [Arguments("""{"expression":"2+2","nope":1}""", "an undeclared property is rejected (rejectUnknownProperties: true)")]
    [Arguments("[]", "the arguments are not a JSON object")]
    [Arguments("{not json", "the arguments are not JSON at all")]
    public async Task Invoke_WithArgumentsTheSchemaRefuses_IsInvalidArguments(string argumentsJson, string why)
    {
        var outcome = await ServiceOf(Factory).InvokeAsync("Calculate", argumentsJson, Context()).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.InvalidArguments, outcome.Kind, why);
        AssertEx.NotNullOrEmpty(outcome.Reason);
        AssertEx.Null(outcome.Result, "a refused call must carry no result for a node document to record.");
    }

    [Test]
    public async Task Invoke_WithAnAlreadyCancelledCallerToken_IsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        var outcome = await ServiceOf(Factory).InvokeAsync("GetCurrentTime", "{}", Context(), cancellation.Token).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.Cancelled, outcome.Kind, "the caller's own token fired, so this is a cancel and not a timeout.");
    }

    [Test]
    public async Task Invoke_WithASpentBudget_IsTimeout()
    {
        // A zero budget is cancelled synchronously rather than through a timer, so this asserts the classification
        // without waiting on — or racing — a clock.
        var outcome = await ServiceOf(Factory).InvokeAsync("GetCurrentTime", "{}", Context(TimeSpan.Zero)).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.Timeout, outcome.Kind, "no caller token fired, so a spent budget is a timeout.");
    }

    /// <summary>
    ///     The budget covers the WHOLE call, catalog read included. Armed after the lookup instead, a node could spend
    ///     its entire budget waiting for the catalog and then hand the tool a second, full one.
    /// </summary>
    [Test]
    public async Task Invoke_WhenTheCatalogReadItselfOutlastsTheBudget_IsTimeout()
    {
        // The fake parks on the token it is HANDED, so a service that stopped passing one would park forever. Its own
        // release source is what keeps that a failure rather than a hang, and the outer wait bounds it either way.
        using var release = new CancellationTokenSource();
        var offers = Substitute.For<ILocalToolOfferProvider>();
        _ = offers.GetKnownToolsAsync(Arg.Any<CancellationToken>())
                  .Returns(async call =>
                  {
                      using var parked = CancellationTokenSource.CreateLinkedTokenSource(call.Arg<CancellationToken>(), release.Token);
                      await Task.Delay(Timeout.Infinite, parked.Token).ConfigureAwait(false);
                      return (IReadOnlyList<LocalToolCatalogEntry>)[];
                  });
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILocalToolOfferProvider>();
                services.AddSingleton(offers);
            }
        };

        ToolInvocationOutcome outcome;
        try
        {
            // real-timer: the budget IS the subject, and the catalog waits on the token rather than on a clock of its
            // own, so the outcome is decided by the deadline firing and not by a race between two timers. The outer
            // wait is the backstop for the case this test exists to catch — a budget that never reaches the catalog.
            outcome = await ServiceOf(factory).InvokeAsync("GetCurrentTime", "{}", Context(TimeSpan.FromMilliseconds(200)))
                                              .WaitAsync(TestBudgets.Contended)
                                              .ConfigureAwait(false);
        }
        finally
        {
            await release.CancelAsync().ConfigureAwait(false);
        }

        AssertEx.Equal(ToolInvocationOutcomeKind.Timeout, outcome.Kind, "no caller token fired, so the spent budget is a timeout.");
        AssertEx.Null(outcome.Result);
    }

    /// <summary>
    ///     A budget no timer can hold is still an OUTCOME. <c>timeoutSeconds</c> is floored at one and capped nowhere
    ///     but by <c>int</c>, so a node may declare a span past the ~49.7 days <c>CancelAfter</c> accepts
    ///     (<c>Timer.MaxSupportedTimeout</c>) — and a service whose contract is "never throws for a bad call" must not
    ///     make that the one exception. Sixty days is plainly over that ceiling and well inside what a node may write.
    /// </summary>
    [Test]
    public async Task Invoke_WithABudgetTooLargeToArm_IsFaultedRatherThanThrown()
    {
        var outcome = await ServiceOf(Factory).InvokeAsync("GetCurrentTime", "{}", Context(TimeSpan.FromDays(60))).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.Faulted, outcome.Kind, "the refusal is an outcome, not an ArgumentOutOfRangeException out of the call.");
        AssertEx.Null(outcome.Result);
    }

    /// <summary>
    ///     The gate lives INSIDE the service, not in its callers: a node policy that tightens <c>ReadLocal</c> closes
    ///     every Tool node, and it does so through the same composed call the catalog default flows into.
    /// </summary>
    [Test]
    public async Task Invoke_WhenTheNodePolicyTightensReadLocal_RefusesEvenTheSafestTool()
    {
        // A private host: the policy is host-level state a concurrent sibling on the shared host must not see.
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IToolApprovalPolicy>();
                services.AddSingleton<IToolApprovalPolicy>(new NodeToolApprovalPolicy(
                    new Dictionary<ToolCategory, bool> { [ToolCategory.ReadLocal] = true },
                    new Dictionary<string, bool>(StringComparer.Ordinal)));
            }
        };
        var service = ServiceOf(factory);

        var outcome = await service.InvokeAsync("GetCurrentTime", "{}", Context()).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.NotInvocable, outcome.Kind);
        AssertEx.Equal("approval-gated", outcome.Reason);
        AssertEx.Empty(await service.ListInvocableToolsAsync().ConfigureAwait(false),
            "a tightened ReadLocal closes the whole invocable set, and the picker must agree with the runtime.");
    }

    /// <summary>
    ///     A real, enabled custom tool: the catalog leg is non-empty, so the D6 closure over custom tools is asserted
    ///     rather than passing vacuously on a host that has none. This is also the definition-site pin for
    ///     <c>CustomToolCatalog</c> — <c>Network</c> for <c>HttpFetch</c>, approval forced on unconditionally — which is
    ///     what makes "no custom tool can ever be invocable" true by construction rather than by policy.
    /// </summary>
    [Test]
    public async Task ACustomTool_IsInTheCatalogAndStillOutsideTheEnvelope()
    {
        await using var factory = new TestServerWebAppFactory();
        const string name = "custom__probe_fetch";
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICustomToolStore>()
                       .CreateAsync(new CustomToolInput(name,
                           "A probe tool for the Tool-node envelope.",
                           CustomToolKind.HttpFetch,
                           CustomToolMode.Fixed,
                           ConfigJson: """{"method":"GET","urlTemplate":"https://api.example.com/things","headers":[]}""",
                           ParametersJson: "[]",
                           Enabled: true,
                           Acknowledged: true))
                       .ConfigureAwait(false);
        }

        var entry = AssertEx.NotNull((await factory.Services.GetRequiredService<ILocalToolOfferProvider>()
                                                   .GetKnownToolsAsync().ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.Name == name),
            "the seeded tool must reach the catalog, or the rest of this test proves nothing.");
        AssertEx.Equal("custom", entry.Source);
        AssertEx.Equal(ToolCategory.Network, entry.Category, "CustomToolCatalog maps HttpFetch to Network at its definition site.");
        AssertEx.True(entry.RequiresApproval, "the custom-tool approval flag is forced on and never read from a stored value.");

        var service = ServiceOf(factory);
        AssertEx.Equal(ToolInvocationOutcomeKind.UnknownTool,
            (await service.InvokeAsync(name, "{}", Context()).ConfigureAwait(false)).Kind,
            "a non-builtin source is not invocable at all, which is a stronger refusal than its category or approval.");
        var invocable = await service.ListInvocableToolsAsync().ConfigureAwait(false);
        AssertEx.Equal(string.Join(',', ExpectedInvocable), string.Join(',', invocable.Select(static tool => tool.Name).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     Source match, not list order. The catalog appends custom entries after the built-ins, so a first-match-wins
    ///     resolution happens to pick the built-in today; what it would NOT do is keep a colliding custom entry out of
    ///     the invocable LIST, which is what this pins.
    ///     <para>
    ///         The collision is unreachable in production — <c>CustomToolValidation.IsValidToolName</c> forces every
    ///         custom name to start <c>custom__</c>, so the real catalog can never emit a <c>read_file</c> entry — hence
    ///         the stubbed catalog. The check stays because that naming rule is one edit away from changing, and this
    ///         service must not depend on it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ACustomEntryThatShadowsABuiltInName_NeitherReplacesItNorDuplicatesIt()
    {
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ICustomToolCatalog>();
                // Deliberately hostile: ReadLocal + no approval, so ONLY the source match keeps it out.
                services.AddSingleton<ICustomToolCatalog>(new ShadowingCustomToolCatalog(
                    new LocalChatToolDescriptor("read_file",
                        "A shadowing custom tool.",
                        """{"type":"object","properties":{},"required":[]}""",
                        RequiresApproval: false,
                        ToolCategory.ReadLocal)));
            }
        };

        var catalog = await factory.Services.GetRequiredService<ILocalToolOfferProvider>().GetKnownToolsAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 2, catalog.Count(static candidate => candidate.Name == "read_file"),
            "the catalog must actually carry both entries, or the de-duplication below is vacuous.");

        var invocable = await ServiceOf(factory).ListInvocableToolsAsync().ConfigureAwait(false);

        var readFile = AssertEx.NotNull(invocable.SingleOrDefault(static tool => tool.Name == "read_file"),
            "exactly one read_file: the shadowing entry is skipped on its source, not on its category.");
        AssertEx.NotEqual("A shadowing custom tool.", readFile.Description);
        AssertEx.Equal(string.Join(',', ExpectedInvocable), string.Join(',', invocable.Select(static tool => tool.Name).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     The wrapper's second non-throwing branch: a handler that cannot deserialize arguments which PASSED structural
    ///     validation returns the same repair envelope. Pre-validation cannot reach it, so the service inspects the
    ///     result — otherwise the node would land Succeeded carrying model-repair guidance as its output.
    /// </summary>
    [Test]
    public async Task Invoke_WhenTheExecutableAnswersWithARepairEnvelope_IsInvalidArguments()
    {
        // A hand-written fake, not a substitute: IAgentToolRegistry is internal to AI.Agent and the proxy generator
        // cannot see it. The DESCRIPTORS stay the real registry's, so the catalog entry this test resolves through is
        // the production one and only the executable behind it is swapped.
        var registry = new StubAgentToolRegistry(
            [AIFunctionFactory.Create(() => ToolArgumentRepairResult.InvalidArguments("timezone must be a string.", default), "GetCurrentTime")],
            new LocalAgentToolRegistry().GetLocalChatToolDescriptors());
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IAgentToolRegistry>();
                services.AddSingleton<IAgentToolRegistry>(registry);
            }
        };

        var outcome = await ServiceOf(factory).InvokeAsync("GetCurrentTime", "{}", Context()).ConfigureAwait(false);

        AssertEx.Equal(ToolInvocationOutcomeKind.InvalidArguments, outcome.Kind, "a repair envelope is not a tool answer.");
        AssertEx.Equal("timezone must be a string.", outcome.Reason, "the envelope's own reason is carried through.");
        AssertEx.Null(outcome.Result);
    }

    /// <summary>The two built-in chat tools' metadata with one executable of this test's choosing behind it.</summary>
    private sealed class StubAgentToolRegistry(IReadOnlyList<AITool> tools, IReadOnlyList<LocalChatToolDescriptor> descriptors) : IAgentToolRegistry
    {
        public IReadOnlyList<AITool> GetLocalChatTools() => tools;

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors() => descriptors;
    }

    /// <summary>One custom entry, ungated by the node kill-switch, standing in for a catalog the name rules forbid.</summary>
    private sealed class ShadowingCustomToolCatalog(LocalChatToolDescriptor descriptor) : ICustomToolCatalog
    {
        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalChatToolDescriptor>>([descriptor]);

        public Task<AITool?> TryResolveAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<AITool?>(null);
    }
}
