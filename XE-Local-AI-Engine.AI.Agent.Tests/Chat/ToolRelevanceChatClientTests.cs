namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The send-time tool-relevance hop. Two families of assertion carry the design. The first is byte-identity: on
///     every path that does not filter the hop returns the CALLER'S OWN options instance, asserted by reference, so no
///     clone and no reordering can slip in. The second is the escape hatch's binding — the decision is bound to the
///     <c>ListToolsFunction</c> object in the INCOMING array, which is the one the function-invoking layer above
///     actually resolves calls against; binding a substitute in the hop's own clone would be invisible in a unit test
///     that only inspected the clone, and would silently do nothing in production.
/// </summary>
public sealed class ToolRelevanceChatClientTests
{
    private const string Query = "please read the project file and summarise it";

    [Test]
    public async Task GetStreamingResponseAsync_WhenTheFeatureIsDisabled_PassesTheOptionsInstanceThrough()
    {
        var (tools, _) = BuildArray(FillerNames(20));
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);
        var options = OptionsFor(tools);

        using (ToolRelevanceScope.BeginScope(active: false, Core()))
        {
            await foreach (var _ in sut.GetStreamingResponseAsync(Conversation(), options))
            {
                // Drained for the side effect: the hop resolves options before the first chunk is pulled.
            }
        }

        AssertEx.True(ReferenceEquals(options, inner.ReceivedOptions.Single()), "The shipped default must be byte-identical, not merely equivalent.");
    }

    [Test]
    public async Task GetResponseAsync_WithNoAmbientScope_PassesTheOptionsInstanceThrough()
    {
        var (tools, _) = BuildArray(FillerNames(20));
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);
        var options = OptionsFor(tools);

        _ = await sut.GetResponseAsync(Conversation(), options);

        AssertEx.True(ReferenceEquals(options, inner.ReceivedOptions.Single()), "The eval and preview runners drive this same client without a scope.");
    }

    [Test]
    public async Task GetResponseAsync_WhenTheAgentOptedOut_PassesTheOptionsInstanceThrough()
    {
        // The per-agent opt-out reaches the hop as an inactive scope, which is the same reference-equality path the
        // node kill-switch takes.
        var (tools, _) = BuildArray(FillerNames(20));
        var sent = await SendAsync(tools, active: false);

        AssertEx.True(sent.PassedThrough);
    }

    [Test]
    public async Task GetResponseAsync_WithTwelveTools_PassesTheOptionsInstanceThrough()
    {
        // The boundary: pass-through at twelve, filtering at thirteen.
        var (tools, _) = BuildArray(FillerNames(11));
        AssertEx.Equal(expected: 12, tools.Count);

        var sent = await SendAsync(tools);

        AssertEx.True(sent.PassedThrough, "At the threshold the whole array is sent unchanged.");
    }

    [Test]
    public async Task GetResponseAsync_WithNoUserMessage_PassesTheOptionsInstanceThrough()
    {
        var (tools, _) = BuildArray(FillerNames(20));
        var sent = await SendAsync(tools, messages: [new ChatMessage(ChatRole.System, "You are helpful.")]);

        AssertEx.True(sent.PassedThrough, "A blank query has nothing to rank against.");
    }

    [Test]
    public async Task GetResponseAsync_WithoutListToolsInTheArray_PassesTheOptionsInstanceThrough()
    {
        // The gate that makes orchestration participants and spawned sub-agents inert BY CONSTRUCTION: only the
        // single-agent factory appends list_tools, so no other array can ever be filtered.
        List<AITool> tools = [.. FillerNames(20).Select(Tool)];

        var sent = await SendAsync(tools);

        AssertEx.True(sent.PassedThrough, "An array with no escape hatch is never filtered.");
    }

    [Test]
    public async Task GetResponseAsync_ForANonCapableModelOffer_PassesTheOptionsInstanceThrough()
    {
        // A non-tool-capable model gets the small built-in-without-agent-home offer. It is below the threshold today,
        // so the pass-through is by size — pinned explicitly rather than left to be inferred from an array length.
        var (tools, _) = BuildArray(["get_time", "calculate", "open_url", "ask_user"]);

        var sent = await SendAsync(tools);

        AssertEx.True(sent.PassedThrough);
    }

    [Test]
    public async Task GetResponseAsync_WithThirteenTools_SendsTwelveAndHidesOne()
    {
        var (tools, listTools) = BuildArray(FillerNames(12));
        AssertEx.Equal(expected: 13, tools.Count);

        var sent = await SendAsync(tools);

        AssertEx.False(sent.PassedThrough, "Above the threshold the hop narrows the array.");
        AssertEx.Equal(expected: 12, sent.Names.Count, "One core (list_tools) plus the eleven-slot fill.");
        AssertEx.Equal(expected: 1, AssertEx.NotNull(listTools.BoundDecision).HiddenNames.Count);
    }

    [Test]
    public async Task GetResponseAsync_OnTheSecondRound_SendsTheIdenticalToolArray()
    {
        var (tools, _) = BuildArray(FillerNames(30));
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(tools));
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(tools));
        }

        var first = NamesOf(inner.ReceivedOptions[0]);
        var second = NamesOf(inner.ReceivedOptions[1]);
        AssertEx.True(first.SequenceEqual(second, StringComparer.Ordinal), "A stable array is what keeps the prompt prefix and the compiled grammar stable across rounds.");
    }

    [Test]
    public async Task GetResponseAsync_WithADifferentToolArray_RecomputesTheDecision()
    {
        var (first, _) = BuildArray(FillerNames(30));
        var (second, _) = BuildArray([.. FillerNames(30).Select(static name => $"other_{name}")]);
        var selector = new CountingSelector();
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner, selector);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(first));
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(second));
        }

        AssertEx.Equal(expected: 2, selector.Invocations, "Every distinct tool array gets its own decision.");
    }

    [Test]
    public async Task GetResponseAsync_WhenTwoArraysHashAlike_KeepsTwoDecisionsAndNeverSharesOne()
    {
        // The hop computes its own key, so a hash collision cannot be forced from here; the forced-collision half is
        // pinned by ToolRelevanceScopeTests.ArrayKey_WhenTwoNameSequencesHashAlike_AreNotEqualAndKeepTwoDecisions.
        // What IS observable here is the property that matters: one array's decision never reaches another's send.
        var (first, _) = BuildArray(FillerNames(30));
        var (second, _) = BuildArray([.. FillerNames(30).Select(static name => $"other_{name}")]);
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(first));
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(second));
        }

        var sentSecond = NamesOf(inner.ReceivedOptions[1]);
        AssertEx.True(sentSecond.All(static name => name.StartsWith("other_", StringComparison.Ordinal) || string.Equals(name, ListToolsFunction.ToolName, StringComparison.Ordinal)),
            "The second array's send must carry only the second array's tools.");
    }

    [Test]
    public async Task GetResponseAsync_AlwaysIncludesAskUserWorkSessionAndApprovalBearingBuiltins()
    {
        string[] core = ["update_work_plan", "record_finding", "save_artifact", "complete_work_session", "run_python"];
        var (tools, _) = BuildArray([.. core, AskUserTool.ToolName, .. FillerNames(20)]);

        var sent = await SendAsync(tools, coreNames: core);

        foreach (var name in core)
        {
            AssertEx.Contains(sent.Names, name);
        }

        AssertEx.Contains(sent.Names, AskUserTool.ToolName);
        AssertEx.Contains(sent.Names, ListToolsFunction.ToolName);
    }

    [Test]
    public async Task GetResponseAsync_NeverRanksAWorkSessionTool()
    {
        string[] workSession = ["update_work_plan", "record_finding", "save_artifact", "complete_work_session"];
        var (tools, listTools) = BuildArray([.. FillerNames(20), .. workSession]);

        // A query that matches nothing would leave the four LAST candidates unranked if they were rankable at all.
        var sent = await SendAsync(tools, coreNames: workSession, query: "zzzz");

        foreach (var name in workSession)
        {
            AssertEx.Contains(sent.Names, name, $"A work-session state tool is never a ranking candidate: {name}.");
            AssertEx.False(AssertEx.NotNull(listTools.BoundDecision).HiddenNames.Contains(name, StringComparer.Ordinal));
        }
    }

    [Test]
    public async Task GetResponseAsync_RanksAnMcpToolLikeAnyOther()
    {
        var (tools, _) = BuildArray([.. FillerNames(20), "mcp_deploy_release"]);

        var hidden = await SendAsync(tools, query: "zzzz");
        AssertEx.False(hidden.Names.Contains("mcp_deploy_release", StringComparer.Ordinal), "An MCP tool is hideable — the amended D6 ruling is the whole point of the slice.");

        var (again, _) = BuildArray([.. FillerNames(20), "mcp_deploy_release"]);
        var offered = await SendAsync(again, query: "deploy the release now");
        AssertEx.Contains(offered.Names, "mcp_deploy_release", "...and it is ranked like everything else, so a query about it wins it a slot.");
    }

    [Test]
    public async Task GetResponseAsync_RanksACustomToolLikeAnyOther()
    {
        var (tools, _) = BuildArray([.. FillerNames(20), "custom__deploy_release"]);

        var hidden = await SendAsync(tools, query: "zzzz");
        AssertEx.False(hidden.Names.Contains("custom__deploy_release", StringComparer.Ordinal));

        var (again, _) = BuildArray([.. FillerNames(20), "custom__deploy_release"]);
        var offered = await SendAsync(again, query: "deploy the release now");
        AssertEx.Contains(offered.Names, "custom__deploy_release");
    }

    [Test]
    public async Task GetResponseAsync_AlwaysIncludesTheMafSkillTools()
    {
#pragma warning disable MAAI001
        string[] skills = [AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName, AgentSkillsProvider.RunSkillScriptToolName];
#pragma warning restore MAAI001
        var (tools, _) = BuildArray([.. FillerNames(20), .. skills]);

        var sent = await SendAsync(tools, query: "zzzz");

        foreach (var name in skills)
        {
            AssertEx.Contains(sent.Names, name, $"A skills agent that cannot see {name} cannot use its skills at all.");
        }
    }

    [Test]
    public async Task GetResponseAsync_WithALargeCoreSet_OffersCorePlusSixAndHidesTheRest()
    {
        // A skills-bearing work-session agent: eight named core tools plus list_tools is a core of nine, which leaves
        // three slots by the bare threshold. The floor raises the fill to six, so the offer is fifteen — above the
        // threshold, because the threshold triggers filtering and does not cap it.
        string[] core = [.. Enumerable.Range(0, 8).Select(static index => $"core_{index}")];
        var (tools, listTools) = BuildArray([.. core, .. FillerNames(20)]);

        var sent = await SendAsync(tools, coreNames: core, query: "zzzz");

        AssertEx.Equal(expected: 15, sent.Names.Count, "Nine core plus the six-slot floor.");
        AssertEx.Equal(expected: 14, AssertEx.NotNull(listTools.BoundDecision).HiddenNames.Count);
    }

    [Test]
    public async Task GetResponseAsync_PinsAToolNamedInTheSystemSeedMessage()
    {
        // Instructions are null on both ROOT agent-build paths, so the instruction text is the leading system message.
        var (tools, _) = BuildArray([.. FillerNames(20), "deploy_service"]);

        var sent = await SendAsync(tools,
            query: "zzzz",
            messages: [new ChatMessage(ChatRole.System, "Always finish by calling deploy_service."), new ChatMessage(ChatRole.User, "zzzz")]);

        AssertEx.Contains(sent.Names, "deploy_service", "A tool the agent's own instructions name verbatim is never hidden.");
    }

    [Test]
    public async Task GetResponseAsync_PinsAToolNamedInOptionsInstructionsForASpawnedChild()
    {
        // A spawned child and an orchestration participant build their own ChatOptions and DO set Instructions.
        var (tools, _) = BuildArray([.. FillerNames(20), "deploy_service"]);
        var options = OptionsFor(tools);
        options.Instructions = "Always finish by calling deploy_service.";

        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);
        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "zzzz")], options);
        }

        AssertEx.Contains(NamesOf(inner.ReceivedOptions.Single()), "deploy_service");
    }

    [Test]
    public async Task GetResponseAsync_WhenTwoRoundsRaceOnOneToolArray_CallsTheSelectorExactlyOnce()
    {
        var (tools, _) = BuildArray(FillerNames(30));
        var selector = new CountingSelector { Gate = new TaskCompletionSource() };
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner, selector);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            var state = AssertEx.NotNull(ToolRelevanceScope.Current);

            // Released TOGETHER off the thread pool, so both rounds really do arrive at the single-flight store at
            // once. Started sequentially on one thread the second caller could only ever find a published entry, and
            // the test would pass against a plain GetOrAdd that runs its factory more than once.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(async () =>
            {
                await start.Task;
                return await sut.GetResponseAsync(Conversation(), OptionsFor(tools));
            });
            var second = Task.Run(async () =>
            {
                await start.Task;
                return await sut.GetResponseAsync(Conversation(), OptionsFor(tools));
            });

            start.SetResult();
            await selector.Entered.Task;
            selector.Gate.SetResult();
            _ = await Task.WhenAll(first, second);

            AssertEx.Equal(expected: 1, selector.Invocations, "One selection per array per turn, not one per round.");
            AssertEx.Equal(expected: 19, Volatile.Read(ref state.PendingNoticeHiddenCount), "The notice counts are added inside the single-flight factory, so a racing round adds nothing.");
            AssertEx.True(NamesOf(inner.ReceivedOptions[0]).SequenceEqual(NamesOf(inner.ReceivedOptions[1]), StringComparer.Ordinal));
        }
    }

    [Test]
    public async Task GetResponseAsync_WhenTheSelectorThrows_SendsTheUnfilteredOptionsInstance()
    {
        // IToolRelevanceSelector is public, so a node-side selector can throw anything at all. The filter is a
        // context-budget optimisation, never an authorisation boundary: a failure costs the saving, not the turn.
        var (tools, _) = BuildArray(FillerNames(30));
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner, new ThrowingSelector());
        var options = OptionsFor(tools);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), options);
        }

        AssertEx.True(ReferenceEquals(options, inner.ReceivedOptions.Single()),
            "A selector failure falls back to the caller's own options instance, byte-identical.");
    }

    [Test]
    public async Task GetResponseAsync_WhenTheCallerCancelsDuringSelection_Throws()
    {
        var (tools, _) = BuildArray(FillerNames(30));
        var selector = new CountingSelector { Gate = new TaskCompletionSource() };
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner, selector);
        using var caller = new CancellationTokenSource();

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            var send = sut.GetResponseAsync(Conversation(), OptionsFor(tools), caller.Token);
            await selector.Entered.Task;
            await caller.CancelAsync();

            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await send);
        }

        AssertEx.Equal(expected: 0, inner.ReceivedOptions.Count, "A cancelled send never reaches the provider.");
    }

    [Test]
    public async Task GetResponseAsync_ForASkillsBearingArray_BindsTheDecisionToTheAppendedListToolsInstance()
    {
        // Built the way MAF composes it: the factory's own tools PLUS the AgentSkillsProvider context tools, in a new
        // list object holding the SAME ListToolsFunction. The binding must land on that object — the one the
        // function-invoking layer resolves against — and not on a substitute in the hop's clone.
#pragma warning disable MAAI001
        string[] skills = [AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName, AgentSkillsProvider.RunSkillScriptToolName];
#pragma warning restore MAAI001
        var (factoryTools, listTools) = BuildArray(FillerNames(20));
        List<AITool> composed = [.. factoryTools, .. skills.Select(Tool)];

        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);
        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(composed));
        }

        var incoming = composed.OfType<ListToolsFunction>().Single();
        AssertEx.True(ReferenceEquals(listTools, incoming));
        AssertEx.NotNull(incoming.BoundDecision, "The decision is bound to the instance in the INCOMING array.");
        AssertEx.True(ReferenceEquals(incoming, AssertEx.NotNull(inner.ReceivedOptions.Single()).Tools!.OfType<ListToolsFunction>().Single()),
            "The clone carries the same instance; substituting a fresh one there would never be invoked.");
    }

    [Test]
    public async Task GetResponseAsync_WhenTheModelCallsListToolsAndTheHopSendsAgain_TheSecondRoundIncludesTheRevealedName()
    {
        var (tools, listTools) = BuildArray(FillerNames(30));
        var selector = new CountingSelector();
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner, selector);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(tools));

            // The model calls the escape hatch — on the instance the hop received, which is the one the function
            // invoking layer would resolve against.
            var listing = await listTools.InvokeAsync(new AIFunctionArguments());
            AssertEx.NotNull(listing as string);

            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(tools));
        }

        var first = NamesOf(inner.ReceivedOptions[0]);
        var second = NamesOf(inner.ReceivedOptions[1]);
        var hidden = AssertEx.NotNull(listTools.BoundDecision).HiddenNames;

        AssertEx.Equal(expected: 1, selector.Invocations, "A reveal changes the array without re-running the selector.");
        foreach (var name in hidden)
        {
            AssertEx.Contains(second, name, "Every revealed tool is callable on the very next round of the same turn.");
        }

        AssertEx.True(second.SequenceEqual(tools.Select(static tool => tool.Name), StringComparer.Ordinal),
            "Every hidden name was revealed, so round two is the whole array — still emitted in INPUT order.");
        AssertEx.True(IsSubsequence(first, second), "Round two preserves round one's ordering, so the prompt prefix does not churn.");
    }

    [Test]
    public async Task GetResponseAsync_WhenTheArrayShapeChangesMidTurn_RebindsToTheNewDecision()
    {
        var (tools, listTools) = BuildArray(FillerNames(20));
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(tools));
            var firstDecision = AssertEx.NotNull(listTools.BoundDecision);

            List<AITool> grown = [.. tools, Tool("late_arrival")];
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(grown));

            AssertEx.False(ReferenceEquals(firstDecision, listTools.BoundDecision), "A changed array is a second key, a second decision, and a rebind.");
        }
    }

    [Test]
    public async Task GetResponseAsync_WhenListToolsIsInvokedWithNoBoundDecision_ReturnsAnEmptyArrayAndDoesNotThrow()
    {
        var (tools, listTools) = BuildArray(FillerNames(5));
        var sent = await SendAsync(tools);

        AssertEx.True(sent.PassedThrough);

        var listing = await listTools.InvokeAsync(new AIFunctionArguments());

        AssertEx.Equal("[]", listing as string, "An unbound invocation is defined, not exceptional.");
    }

    [Test]
    public async Task GetResponseAsync_WhenANestedSubAgentSendRunsUnderTheSameScope_LeavesTheParentBindingIntact()
    {
        var (parentTools, listTools) = BuildArray(FillerNames(20));
        List<AITool> childTools = [.. FillerNames(20).Select(name => Tool($"child_{name}"))];
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(parentTools));
            var parentDecision = AssertEx.NotNull(listTools.BoundDecision);

            // A spawned sub-agent runs under the parent's AsyncLocal, but its approval-stripped set carries no
            // list_tools — so it reaches no binding and cannot disturb the parent's.
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(childTools));

            AssertEx.True(ReferenceEquals(parentDecision, listTools.BoundDecision));
            AssertEx.Equal(childTools.Count, NamesOf(inner.ReceivedOptions[1]).Count, "The child sends its own COMPLETE tool array.");
        }
    }

    [Test]
    public async Task GetResponseAsync_WhenTheSendIsCancelledDuringSelection_KeepsTheDecisionSoTheRetryReusesIt()
    {
        var (tools, _) = BuildArray(FillerNames(30));
        var selector = new CountingSelector { Gate = new TaskCompletionSource() };
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner, selector);
        using var abandoned = new CancellationTokenSource();

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            var first = sut.GetResponseAsync(Conversation(), OptionsFor(tools), abandoned.Token);
            await abandoned.CancelAsync();
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await first);

            selector.Gate.SetResult();

            // The pre-first-token retry re-invokes the whole send factory; it must reuse the decision.
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(tools));
        }

        AssertEx.Equal(expected: 1, selector.Invocations, "A caller's cancelled wait evicts nothing.");
    }

    [Test]
    public async Task GetResponseAsync_OnAnApprovalResumeRound_SendsTheSameNarrowedArrayAndKeepsTheBinding()
    {
        // The runner replays history plus ONE user message whose only content is ToolApprovalResponseContent. That
        // message has no text, so a per-send query re-derivation resolved blank and the whole array went back out
        // mid-turn: hidden tools reappeared, the prompt prefix churned and list_tools still answered from the first
        // round's decision. One decision per array per turn is what makes the resume round identical to the first.
        var (tools, listTools) = BuildArray(FillerNames(30));
        var selector = new CountingSelector();
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner, selector);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), OptionsFor(tools));
            var firstDecision = AssertEx.NotNull(listTools.BoundDecision);

            _ = await sut.GetResponseAsync(ApprovalResumeConversation(), OptionsFor(tools));

            AssertEx.Equal(expected: 1, selector.Invocations, "The resume round reuses the array's decision instead of computing a second one.");
            AssertEx.True(ReferenceEquals(firstDecision, listTools.BoundDecision), "list_tools must not be left answering from a decision the model is no longer looking at.");
        }

        var first = NamesOf(inner.ReceivedOptions[0]);
        var resumed = NamesOf(inner.ReceivedOptions[1]);

        AssertEx.False(ReferenceEquals(inner.ReceivedOptions[0], inner.ReceivedOptions[1]), "Each send narrows its own options clone.");
        AssertEx.True(first.SequenceEqual(resumed, StringComparer.Ordinal), "The approval round-trip must not re-expose a single hidden tool.");
        AssertEx.True(resumed.Count < tools.Count, "The resume round is still narrowed, not the full array.");
    }

    [Test]
    public async Task GetResponseAsync_WithOnlyATextLessUserMessageAndNoDecision_PassesTheOptionsInstanceThrough()
    {
        // The other half of the same rule: reuse is what a text-less round gets, never a decision computed from a
        // blank query. With nothing yet decided for this array the hop takes the byte-identical path exactly as it
        // does for a conversation with no user message at all.
        var (tools, listTools) = BuildArray(FillerNames(30));

        var sent = await SendAsync(tools, messages: ApprovalResumeConversation(userText: null));

        AssertEx.True(sent.PassedThrough, "A round with no text to rank against and no decision to reuse is unchanged.");
        AssertEx.Null(listTools.BoundDecision, "Nothing was decided, so nothing was bound.");
    }

    [Test]
    public async Task GetResponseAsync_WhenTheSelectorThrows_NeverHandsTheExceptionToTheLogSink()
    {
        // Sinks render Exception.Message and every inner exception. The selector call carries the query and the tool
        // descriptions, so a transport error echoing its request body would write raw trajectory content to disk
        // under a template deliberately scrubbed to counts. The type name is the whole allowed diagnosis.
        const string Secret = "SECRET-TRAJECTORY-MARKER";
        var (tools, _) = BuildArray(FillerNames(30));
        var logger = new CapturingLogger<ToolRelevanceChatClient>();
        using var inner = new CapturingChatClient();
        using var sut = new ToolRelevanceChatClient(inner,
            new ThrowingSelector($"the selector broke on {Secret}", new InvalidOperationException($"inner {Secret}")),
            new ToolRelevanceOptions(),
            logger);
        var options = OptionsFor(tools);

        using (ToolRelevanceScope.BeginScope(active: true, Core()))
        {
            _ = await sut.GetResponseAsync(Conversation(), options);
        }

        var entry = logger.Entries.Single();
        AssertEx.True(ReferenceEquals(options, inner.ReceivedOptions.Single()), "A selector failure costs the saving and nothing else.");
        AssertEx.Null(entry.Exception, "The exception object must never reach the sink — the sink is what renders its message.");
        AssertEx.False(entry.Message.Contains(Secret, StringComparison.Ordinal), "No exception text may reach the log.");
        AssertEx.True(entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal), "The failure TYPE is the allowed diagnosis.");
    }

    // The runner's approval-resume shape: history, then one user message carrying only ToolApprovalResponseContent.
    private static IReadOnlyList<ChatMessage> ApprovalResumeConversation(string? userText = Query)
    {
        var call = new FunctionCallContent("call-1", "tool_0");
        var response = new ToolApprovalRequestContent("call-1", call).CreateResponse(approved: true, "Approved by user.");

        List<ChatMessage> messages = [new ChatMessage(ChatRole.System, "You are helpful.")];
        if (userText is not null)
        {
            messages.Add(new ChatMessage(ChatRole.User, userText));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, [call]));
        messages.Add(new ChatMessage(ChatRole.User, [response]));
        return messages;
    }

    // Whether every element of `inner` appears in `outer` in the same relative order.
    private static bool IsSubsequence(IReadOnlyList<string> inner, IReadOnlyList<string> outer)
    {
        var cursor = 0;
        foreach (var name in outer)
        {
            if (cursor < inner.Count && string.Equals(inner[cursor], name, StringComparison.Ordinal))
            {
                cursor++;
            }
        }

        return cursor == inner.Count;
    }

    private static ToolRelevanceChatClient BuildSut(IChatClient inner, IToolRelevanceSelector? selector = null)
    {
        return new ToolRelevanceChatClient(inner,
            selector ?? new LexicalToolRelevanceSelector(),
            new ToolRelevanceOptions(),
            NullLogger<ToolRelevanceChatClient>.Instance);
    }

    private static async Task<SentArray> SendAsync(IList<AITool> tools,
        bool active = true,
        string[]? coreNames = null,
        string query = Query,
        IReadOnlyList<ChatMessage>? messages = null)
    {
        using var inner = new CapturingChatClient();
        using var sut = BuildSut(inner);
        var options = OptionsFor(tools);

        using (ToolRelevanceScope.BeginScope(active, Core(coreNames ?? [])))
        {
            _ = await sut.GetResponseAsync(messages ?? Conversation(query), options);
        }

        var received = inner.ReceivedOptions.Single();
        return new SentArray(ReferenceEquals(options, received), NamesOf(received));
    }

    private static (List<AITool> Tools, ListToolsFunction ListTools) BuildArray(IReadOnlyList<string> names)
    {
        var tools = new List<AITool>();
        var listTools = new ListToolsFunction(tools);
        tools.Add(listTools);
        tools.AddRange(names.Select(Tool));
        return (tools, listTools);
    }

    private static AITool Tool(string name)
    {
        return AIFunctionFactory.Create(() => "ok", name, $"The {name} tool.");
    }

    private static string[] FillerNames(int count)
    {
        return [.. Enumerable.Range(0, count).Select(static index => $"tool_{index}")];
    }

    private static ChatOptions OptionsFor(IList<AITool> tools)
    {
        return new ChatOptions
        {
            Tools = tools
        };
    }

    private static IReadOnlyList<ChatMessage> Conversation(string query = Query)
    {
        return
        [
            new ChatMessage(ChatRole.System, "You are helpful."),
            new ChatMessage(ChatRole.User, query)
        ];
    }

    private static IReadOnlySet<string> Core(params string[] names)
    {
        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    private static List<string> NamesOf(ChatOptions? options)
    {
        return [.. (options?.Tools ?? []).Select(static tool => tool.Name)];
    }

    private sealed record SentArray(bool PassedThrough, List<string> Names);

    // Stands in for a node-side selector that breaks in a way the hop cannot anticipate. The message and inner
    // exception are settable so the privacy test can plant a marker and prove neither reaches the sink.
    private sealed class ThrowingSelector(string message = "the selector broke", Exception? inner = null) : IToolRelevanceSelector
    {
        public Task<ToolRelevanceSelection> SelectAsync(string? query,
            IReadOnlyList<ToolRelevanceCandidate> candidates,
            int threshold,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(message, inner);
        }
    }

    // Keeps the exception argument as well as the formatted message: a template scrubbed to counts proves nothing if
    // the exception object rides alongside it, because the sink renders that too.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Entries.Add(new Entry(formatter(state, exception), exception));
        }

        public sealed record Entry(string Message, Exception? Exception);
    }

    // Counts selections and, when gated, blocks every caller inside the shared factory so a race is observable.
    private sealed class CountingSelector : IToolRelevanceSelector
    {
        private readonly LexicalToolRelevanceSelector _inner = new();
        private int _invocations;

        public TaskCompletionSource? Gate { get; init; }

        /// <summary>Signals that the shared computation is in flight, so a racing test can release its gate without a sleep.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Invocations => Volatile.Read(ref _invocations);

        public async Task<ToolRelevanceSelection> SelectAsync(string? query,
            IReadOnlyList<ToolRelevanceCandidate> candidates,
            int threshold,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _invocations);
            _ = Entered.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return await _inner.SelectAsync(query, candidates, threshold, cancellationToken);
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatOptions?> ReceivedOptions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
