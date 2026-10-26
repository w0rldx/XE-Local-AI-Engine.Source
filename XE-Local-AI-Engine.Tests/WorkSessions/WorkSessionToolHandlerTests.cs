namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The four state tools against the real store. The session is resolved from the ambient conversation id the
///     invocation runner seeds, so every case here stages that scope — or deliberately does not, which is the
///     fail-closed path that makes the profile-opt-in offer safe.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class WorkSessionToolHandlerTests
{
    [ClassDataSource<WorkSessionHostFixture>(Shared = SharedType.PerClass)]
    public required WorkSessionHostFixture Host { get; init; }

    [Test]
    public async Task UpdateWorkPlan_AddsAndCompletesTasks()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            var added = await handler.ExecuteAsync("""{"operations":[{"op":"add","title":"Read the runtime docs","status":"Active"}]}""");
            AssertEx.Contains(added, "1 work-plan change");
        }

        var tasks = await ReadTasksAsync(factory, sessionId);
        var task = AssertEx.NotNull(tasks.SingleOrDefault(), "The batch adds exactly one task.");
        AssertEx.Equal("Read the runtime docs", task.Title);
        AssertEx.Equal(AgentWorkSessionTaskStatus.Active, task.Status);
        AssertEx.Equal(AgentWorkSessionTaskOrigin.Agent, task.Origin, "A tool-authored task is the agent's, never the user's.");

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync($$"""{"operations":[{"op":"complete","taskId":"{{task.Id}}"}]}""");
        }

        AssertEx.Equal(AgentWorkSessionTaskStatus.Done, (await ReadTasksAsync(factory, sessionId)).Single().Status);
    }

    [Test]
    public async Task UpdateWorkPlan_MovesTheSessionsCurrentTaskPointer_SoTheDetailReadIsNotStale()
    {
        // The pointer used to move only on a status transition, so it went stale for the rest of a multi-step run every
        // time the agent switched tasks — and WorkSessionDetail.CurrentTaskId is what the session page reads.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync("""{"operations":[{"op":"add","title":"First","status":"Active"},{"op":"add","title":"Second"}]}""");
        }

        var tasks = await ReadTasksAsync(factory, sessionId);
        var first = tasks.Single(task => task.Title == "First");
        var second = tasks.Single(task => task.Title == "Second");
        AssertEx.Equal(first.Id, await ReadDetailCurrentTaskAsync(factory, sessionId));

        // Switching the active task mid-run must move the pointer with it.
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync($$"""{"operations":[{"op":"complete","taskId":"{{first.Id}}"},{"op":"update","taskId":"{{second.Id}}","status":"Active"}]}""");
        }

        AssertEx.Equal(second.Id, await ReadDetailCurrentTaskAsync(factory, sessionId));

        // Finishing the current task leaves no pointer rather than one aimed at finished work.
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync($$"""{"operations":[{"op":"complete","taskId":"{{second.Id}}"}]}""");
        }

        AssertEx.Null(await ReadDetailCurrentTaskAsync(factory, sessionId));
    }

    [Test]
    [Arguments("name")]
    [Arguments("text")]
    [Arguments("summary")]
    public async Task UpdateWorkPlan_Add_AcceptsATitleAlias(string alias)
    {
        // A 27B model reached for these keys instead of 'title' and, when the unknown key failed the whole batch at
        // deserialization, burned the step's entire provider-call budget retrying.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync($$"""{"operations":[{"op":"add","{{alias}}":" Read the runtime docs "}]}"""),
                "1 work-plan change");
        }

        AssertEx.Equal("Read the runtime docs",
            (await ReadTasksAsync(factory, sessionId)).Single().Title,
            $"'{alias}' is an alias for 'title', trimmed like one.");
    }

    [Test]
    public async Task UpdateWorkPlan_Add_WhenNoTitleOrAlias_ReturnsSentenceWithExample()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"operations":[{"op":"add"}]}""");

        AssertEx.Contains(result, "needs a title");
        AssertEx.Contains(result, "\"op\":\"add\"", message: "The sentence carries a shape the model can copy, not just a complaint.");
    }

    [Test]
    public async Task UpdateWorkPlan_WhenAKeyIsUnknown_ReturnsTheExampleAndNoClrTypeName()
    {
        // Valid JSON, wrong shape: the deserializer rejects the unknown key for the whole batch, and its own message
        // names the CLR request type — useless to a model and the thing that used to be echoed straight back.
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"operations":[{"op":"add","label":"Investigate"}]}""");

        AssertEx.Contains(result, WorkSessionToolDefinitions.UpdateWorkPlan.ExampleArguments);
        AssertEx.False(result.Contains("WorkPlanOperationRequest", StringComparison.Ordinal), "The parser's message is for the log, never for the model.");
    }

    [Test]
    public async Task UpdateWorkPlan_WhenAnOperationIsUnknown_ReturnsAnActionableSentence()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"operations":[{"op":"annihilate"}]}""");

        AssertEx.Contains(result, "must be one of add, update, complete or drop");
    }

    [Test]
    public async Task UpdateWorkPlan_WhenATitleIsOverLength_ReturnsTheBoundSentence()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync($$"""{"operations":[{"op":"add","title":"{{new string('t', 400)}}"}]}""");

        AssertEx.Contains(result, "exceeded the maximum length");
    }

    [Test]
    public async Task UpdateWorkPlan_Add_NamesTheNewTaskIds_SoTheSameStepCanMoveThem()
    {
        // A live finding: the step's state block is composed before the added tasks exist, so in a one-step session
        // the add result is the only place the model can learn an id. Without it no task can be marked Blocked, and
        // the workflow executor's Blocked-task signal cannot fire on a single-step node at all.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        string added;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            added = await handler.ExecuteAsync("""{"operations":[{"op":"add","title":"Obtain approval"},{"op":"add","title":"Write choice.md"}]}""");
        }

        AssertEx.Contains(added, "Added 2 task(s):");
        foreach (var task in await ReadTasksAsync(factory, sessionId))
        {
            AssertEx.Contains(added,
                $"\"{task.Title}\" = {task.Id}",
                message: "The result names every added task with the id in the spelling the state block prints.");
        }

        // Read one id back out of the result exactly as a model would, then move that task with no state block to consult.
        var approvalId = IdFromAddResult(added, "Obtain approval");
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler
                .ExecuteAsync($$"""{"operations":[{"op":"update","taskId":"{{approvalId}}","status":"Blocked","blockedReason":"No operator answered."}]}""");
        }

        var blocked = (await ReadTasksAsync(factory, sessionId)).Single(task => task.Title == "Obtain approval");
        AssertEx.Equal(AgentWorkSessionTaskStatus.Blocked, blocked.Status, "An id taken from the add result is one 'update' accepts.");
        AssertEx.Equal("No operator answered.", blocked.BlockedReason);
    }

    [Test]
    public async Task UpdateWorkPlan_Add_RetriedByteIdenticallyInTheSameStep_AddsTheTaskOnceAndNamesTheSameId()
    {
        // The id used to be a fresh Guid, and DescribeBatch folds it into the batch's idempotency key — so a retry
        // after a lost response computed a DIFFERENT key, the store's dedupe saw a new operation, and the same add
        // landed as a second task row.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);
        const string Arguments = """{"operations":[{"op":"add","title":"Read the runtime docs","detail":"Start at the pin","status":"Active"}]}""";

        string first;
        string retry;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            first = await handler.ExecuteAsync(Arguments);
            retry = await handler.ExecuteAsync(Arguments);
        }

        var tasks = await ReadTasksAsync(factory, sessionId);
        AssertEx.Equal(expected: 1, tasks.Count, "A byte-identical retry of the same batch commits once.");
        var task = tasks[0];
        AssertEx.Equal(IdFromAddResult(first, "Read the runtime docs"),
            IdFromAddResult(retry, "Read the runtime docs"),
            "The retry names the id the first call minted, so an id the model already holds stays the one that exists.");
        AssertEx.Contains(first, $"\"Read the runtime docs\" = {task.Id}");
    }

    [Test]
    public async Task UpdateWorkPlan_ASecondBatchRepeatingAnEarlierAdd_StillRecordsTheNewOne()
    {
        // A model restating its whole plan is ordinary. When an add's id was derived from its own content alone, the
        // repeat re-minted an id the table already held, the store refused the batch as a duplicate row, and the
        // genuinely new add beside it was rolled back with a "try the same call once more" sentence that could never
        // succeed for the rest of the step.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        string second;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync("""{"operations":[{"op":"add","title":"Investigate the pin"}]}""");
            second = await handler.ExecuteAsync("""{"operations":[{"op":"add","title":"Investigate the pin"},{"op":"add","title":"Write the report"}]}""");
        }

        AssertEx.Contains(second, "Added 2 task(s):");
        var titles = (await ReadTasksAsync(factory, sessionId)).Select(static task => task.Title).ToList();
        AssertEx.Contains(titles, "Write the report", "The new add commits rather than being rolled back with the repeat.");
    }

    [Test]
    public async Task UpdateWorkPlan_TwoIdenticalAddsInOneBatch_StayTwoTasks()
    {
        // The id is derived from the batch position as well as the content, so a plan that deliberately repeats a step
        // is not silently collapsed into one task by the same determinism that makes the retry safe.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        string added;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            added = await handler.ExecuteAsync("""{"operations":[{"op":"add","title":"Run the gate"},{"op":"add","title":"Run the gate"}]}""");
        }

        var tasks = await ReadTasksAsync(factory, sessionId);
        AssertEx.Equal(2, tasks.Count, "Two adds are two tasks even when they carry the same content.");
        AssertEx.Equal(2, tasks.Select(static task => task.Id).Distinct().Count(), "Their ids differ by batch position.");
        foreach (var task in tasks)
        {
            AssertEx.Contains(added, $"\"Run the gate\" = {task.Id}");
        }
    }

    [Test]
    public async Task UpdateWorkPlan_Add_BeyondTheListedBound_CountsTheRestRatherThanNamingThem()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);
        var operations = string.Join(',', Enumerable.Range(1, 13).Select(static index => $$"""{"op":"add","title":"Task {{index}}"}"""));

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync($$"""{"operations":[{{operations}}]}""");

        AssertEx.Contains(result, "Added 13 task(s):");
        AssertEx.Contains(result, "\"Task 10\" = ");
        AssertEx.Contains(result, ", +3 more.");
        AssertEx.True(!result.Contains("\"Task 11\" = ", StringComparison.Ordinal),
            "The bound stops the list, so a maximal batch cannot spend the step's context echoing itself.");
    }

    [Test]
    public async Task RecordFinding_WritesTheRowAndPublishesItsWatermark()
    {
        var publisher = new RecordingWorkSessionEventPublisher();
        // Private host: the recording publisher is per-test state, so sharing it would let siblings' publishes bleed in.
        await using var factory = NewFactory(publisher);
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.RecordFinding.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"kind":"Decision","text":"Use the process sandbox.","sourceRef":"docs/adr-0004"}""");

        AssertEx.Contains(result, "Recorded a Decision");
        var finding = (await WorkSessionTestSupport.ReadFindingsAsync(factory.Services, sessionId)).Single();
        AssertEx.Equal(AgentWorkSessionFindingKind.Decision, finding.Kind);
        AssertEx.Equal("docs/adr-0004", finding.SourceRef);
        // The published watermark is the event row's, which the store stamps just after the finding's — a subscriber is
        // told "something changed at N" and re-reads each feed from that feed's own watermark.
        AssertEx.Contains(publisher.Published,
            published => published.Kind == WorkSessionChangeKind.Finding && published.Sequence > finding.Sequence,
            "Recording a finding announces the watermark its commit allocated.");
    }

    [Test]
    public async Task RecordFinding_WhenTheKindIsUnknown_ReturnsTheEnumSentence()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var handler = Handler(factory, WorkSessionToolDefinitions.RecordFinding.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        AssertEx.Contains(await handler.ExecuteAsync("""{"kind":"Rumour","text":"Something."}"""),
            "must be one of Finding, Evidence, Decision or OpenQuestion");
    }

    [Test]
    public async Task SaveArtifact_WritesTheBlobThenTheRow_AndReplacesByName()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.SaveArtifact.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"name":"report.md","mediaType":"text/markdown","kind":"Report","text":"first"}"""),
                "Saved artifact 'report.md'");
            _ = await handler.ExecuteAsync("""{"name":"report.md","mediaType":"text/markdown","kind":"Report","text":"second"}""");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var artifacts = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListArtifactsAsync(sessionId);
        var artifact = AssertEx.NotNull(artifacts.SingleOrDefault(), "Saving under an existing name replaces it rather than adding a second row.");
        AssertEx.Equal(expected: 6, artifact.SizeBytes);

        var content = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().ReadArtifactContentAsync(sessionId, artifact.Id);
        AssertEx.Equal("second", content.Content);
        AssertEx.False(content.IsBase64, "A text media type comes back as text.");
    }

    [Test]
    public async Task SaveArtifact_WhenBothOrNeitherPayloadIsGiven_Refuses()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var handler = Handler(factory, WorkSessionToolDefinitions.SaveArtifact.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        AssertEx.Contains(await handler.ExecuteAsync("""{"name":"a","mediaType":"text/plain","kind":"Note"}"""), "exactly one of 'text' or 'base64'");
        AssertEx.Contains(await handler.ExecuteAsync("""{"name":"a","mediaType":"text/plain","kind":"Note","text":"x","base64":"eA=="}"""),
            "exactly one of 'text' or 'base64'");
    }

    [Test]
    public async Task SaveArtifact_WhenTheContentIsOverTheNodeCap_RefusesWithoutWritingARow()
    {
        // Private host: the artifact cap it asserts on is a host-level config value.
        await using var factory = NewFactory(configuration: ("WorkSessions:MaxArtifactBytes", "32"));
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.SaveArtifact.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync($$"""{"name":"big","mediaType":"text/plain","kind":"Note","text":"{{new string('x', 64)}}"}"""),
                "over this node's");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        AssertEx.Empty(await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListArtifactsAsync(sessionId));
    }

    [Test]
    public async Task CompleteWorkSession_RecordsTheRequestWithoutTerminalizingTheSession()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"summary":"Everything asked for is recorded."}"""), "close at the end of this turn");
        }

        AssertEx.Equal(AgentWorkSessionStatus.Draft,
            (await WorkSessionTestSupport.ReadSessionAsync(factory.Services, sessionId)).Status,
            "The tool never terminalizes: the turn has to finish cleanly and the supervisor closes the session.");
        AssertEx.Contains(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId),
            entry => entry.EventType == WorkSessionEventTypes.CompletionRequested);
    }

    /// <summary>
    ///     The honesty argument, both ways. It is written onto the completion EVENT because that is where the workflow
    ///     executor reads it back from when it decides whether the node run succeeded or has to stand down.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CompleteWorkSession_CarriesObjectiveMetOntoTheEventDetail(bool objectiveMet)
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync($$"""{"summary":"What it came to.","objectiveMet":{{(objectiveMet ? "true" : "false")}}}""");
        }

        AssertEx.Equal(objectiveMet,
            AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId))).ObjectiveMet,
            "The declaration is the whole point of the argument, so it has to survive to the event.");
    }

    [Test]
    public async Task CompleteWorkSession_WithoutObjectiveMet_DeclaresNothing()
    {
        // Absent is not "not met": every completion recorded before the argument existed is one of these, and reading
        // them as unmet would retroactively block node runs whose sessions finished cleanly.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync("""{"summary":"Everything asked for is recorded."}""");
        }

        AssertEx.Null(AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId))).ObjectiveMet);
    }

    /// <summary>
    ///     Under an output contract a completion without a valid output is a tool error and records nothing, so the session stays
    ///     open and the model corrects itself in the next call. A nested object is one of the invalid shapes.
    /// </summary>
    [Test]
    [Arguments("""{"summary":"Done."}""", "needs an 'output' object")]
    [Arguments("""{"summary":"Done.","output":{"kind":"defect"}}""", "\"defect\"")]
    [Arguments("""{"summary":"Done.","output":{"kind":{"label":"bug"}}}""", "'output.kind' must be")]
    [Arguments("""{"summary":"Done.","output":{"kind":"bug","severity":"high"}}""", "carries 'severity'")]
    [Arguments("""{"summary":"Done.","output":{"rationale":"no kind"}}""", "missing the required member 'kind'")]
    [Arguments("""{"summary":"Done.","output":["bug"]}""", "must be a flat object")]
    public async Task CompleteWorkSession_UnderAnOutputContract_RefusesAMissingOrInvalidOutput(string arguments, string expected)
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        string result;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(KindContract))
        {
            result = await handler.ExecuteAsync(arguments);
        }

        AssertEx.Contains(result, "stays open");
        AssertEx.Contains(result, expected);
        AssertEx.False((await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId))
            .Any(static entry => entry.EventType == WorkSessionEventTypes.CompletionRequested),
            "A refused completion must not close the session.");
    }

    [Test]
    public async Task CompleteWorkSession_UnderAnOutputContract_RecordsAValidOutputOnTheEventDetail()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(KindContract))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"summary":"Triaged.","output":{"kind":"bug","rationale":"It crashes."}}"""), "close at the end of this turn");
        }

        var output = AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId))).Output;
        AssertEx.True(output.HasValue, "a valid output is recorded on the event.");
        AssertEx.Equal("bug", output!.Value.GetProperty("kind").GetString());
    }

    /// <summary>An honest unmet close owes no output: it stands the node run down for a person, and demanding one invites an invented answer.</summary>
    [Test]
    public async Task CompleteWorkSession_UnderAnOutputContract_AcceptsAnUnmetCloseWithoutOutput()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(KindContract))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"summary":"The request names nothing I can classify.","objectiveMet":false}"""), "close at the end of this turn");
        }

        AssertEx.Equal(false, AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId))).ObjectiveMet);
    }

    /// <summary>An unmet close carrying a VALID output keeps it: a refused verdict handed in beside objectiveMet false is the reason the node blocks on.</summary>
    [Test]
    public async Task CompleteWorkSession_UnderAnOutputContract_KeepsTheValidOutputOfAnUnmetClose()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(KindContract))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"summary":"Not a bug I can stand behind.","objectiveMet":false,"output":{"kind":"unclear"}}"""),
                "close at the end of this turn");
        }

        var detail = AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId)));
        AssertEx.Equal(false, detail.ObjectiveMet);
        AssertEx.True(detail.Output.HasValue, "a valid output on an unmet close is recorded.");
        AssertEx.Equal("unclear", detail.Output!.Value.GetProperty("kind").GetString());
    }

    /// <summary>An unmet close is never refused over its output, but one that does not match the contract is not recorded as routing data.</summary>
    [Test]
    public async Task CompleteWorkSession_UnderAnOutputContract_DropsTheOutputOfAnUnmetClose()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(KindContract))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"summary":"Could not classify it.","objectiveMet":false,"output":{"kind":"defect"}}"""),
                "close at the end of this turn");
        }

        var detail = AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId)));
        AssertEx.Equal(false, detail.ObjectiveMet);
        AssertEx.Null(detail.Output, "an output nobody validated must not be recorded under the contract.");
    }

    /// <summary>Without a contract the member is ignored, even malformed: a session outside a workflow records what it always did.</summary>
    [Test]
    [Arguments("""{"summary":"Done.","output":{"kind":"bug"}}""")]
    [Arguments("""{"summary":"Done.","output":{"kind":{"label":"bug"}}}""")]
    public async Task CompleteWorkSession_WithoutAnOutputContract_IgnoresTheOutput(string arguments)
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync(arguments), "close at the end of this turn");
        }

        AssertEx.Null(AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId))).Output);
    }

    /// <summary>A completion recorded before the member existed reads with no output rather than failing to read.</summary>
    [Test]
    public void CompletionDetail_RecordedBeforeTheOutputMember_ReadsAsNoOutput() =>
        AssertEx.Null(AssertEx.NotNull(JsonSerializer.Deserialize<WorkSessionCompletionDetail>("""{"Summary":"Done.","ObjectiveMet":true}""")).Output);

    [Test]
    public async Task CompleteWorkSession_WhenAKeyIsUnknown_StillRejectsTheWholeCall()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"summary":"Done.","objectiveWasMet":false}"""),
                WorkSessionToolDefinitions.CompleteWorkSession.ExampleArguments);
        }

        AssertEx.False((await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId))
            .Any(static entry => entry.EventType == WorkSessionEventTypes.CompletionRequested),
            "A call the deserializer refused must not close the session.");
    }

    /// <summary>
    ///     The read tool pages by characters off the offset the last page handed back, and says where the artifact ends, so a
    ///     model reading a diff larger than one page reaches its last line by following nextOffset alone.
    /// </summary>
    [Test]
    public async Task ReadUpstreamArtifact_PagesThroughTheArtifactByTheOffsetEachPageHandsBack()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var page = WorkSessionToolDefinitions.ReadUpstreamArtifact.PageCharacters;
        var artifact = await ReadableAsync(factory, "validate.patch", "validate", new string('a', page) + new string('b', page) + "tail");
        var handler = Handler(factory, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName);

        string first, second, last;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(readableArtifacts: [artifact]))
        {
            first = await handler.ExecuteAsync("""{"name":"validate.patch"}""");
            second = await handler.ExecuteAsync($$"""{"name":"validate.patch","offset":{{page}}}""");
            last = await handler.ExecuteAsync($$"""{"name":"validate.patch","offset":{{2 * page}}}""");
        }

        AssertEx.Contains(first, $"offset=0 length={page} total={(2 * page) + 4} nextOffset={page}");
        AssertEx.Contains(first, new string('a', page));
        AssertEx.False(first.Contains('b', StringComparison.Ordinal), "a page stops at the page size.");
        AssertEx.Contains(second, $"offset={page} length={page} total={(2 * page) + 4} nextOffset={2 * page}");
        AssertEx.Contains(second, new string('b', page));
        AssertEx.Contains(last, "length=4 total=");
        AssertEx.Contains(last, "nextOffset=null (end of artifact)");
        AssertEx.True(last.EndsWith("tail", StringComparison.Ordinal), "the last page ends where the artifact does.");
    }

    /// <summary>
    ///     Every page served is recorded as an <c>ArtifactRead</c> event the workflow lane counts coverage from, and the same page asked
    ///     twice in one step is recorded once, so a replayed step cannot inflate what was read.
    /// </summary>
    [Test]
    public async Task ReadUpstreamArtifact_RecordsEachServedPageOnceAsAnArtifactReadEvent()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var page = WorkSessionToolDefinitions.ReadUpstreamArtifact.PageCharacters;
        var artifact = await ReadableAsync(factory, "validate.patch", "validate", new string('a', page) + "tail");
        var handler = Handler(factory, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(readableArtifacts: [artifact]))
        {
            _ = await handler.ExecuteAsync("""{"name":"validate.patch"}""");
            _ = await handler.ExecuteAsync("""{"name":"validate.patch","offset":0}""");
            _ = await handler.ExecuteAsync($$"""{"name":"validate.patch","offset":{{page}}}""");
            _ = await handler.ExecuteAsync("""{"name":"secrets.env"}""");
        }

        var reads = (await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId))
                    .Where(static entry => entry.EventType == WorkSessionEventTypes.ArtifactRead)
                    .Select(static entry => AssertEx.NotNull(JsonSerializer.Deserialize<WorkSessionArtifactReadDetail>(AssertEx.NotNull(entry.DetailJson))))
                    .ToList();
        AssertEx.Equal($"0+{page}, {page}+4", string.Join(", ", reads.Select(static read => $"{read.Offset}+{read.Length}")),
            "one event per distinct page served, and none for a refused read.");
        AssertEx.True(reads.TrueForAll(read => read.ArtifactId == artifact.ArtifactId && read.Total == page + 4 && read.Producer == "validate"),
            "each event names the artifact and its whole length, so coverage can be judged without the bytes.");
    }

    /// <summary>Under a result budget below the page size each page fits it, so the wrapper never cuts a page nextOffset moved past.</summary>
    [Test]
    public async Task ReadUpstreamArtifact_UnderATightResultBudget_PagesFitTheBudgetAndRebuildTheArtifact()
    {
        const int budget = 1_500;
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var content = string.Concat(Enumerable.Range(0, 900).Select(static line => $"+line {line}\n"));
        var artifact = await ReadableAsync(factory, "validate.patch", "validate", content);
        var handler = Handler(factory, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName);
        var separator = Environment.NewLine + Environment.NewLine;

        var rebuilt = new StringBuilder();
        var pages = new List<string>();
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(readableArtifacts: [artifact]))
        using (ToolResultBudgetScope.BeginScope(budget))
        {
            var offset = "0";
            while (offset is not null && pages.Count < 100)
            {
                var page = await handler.ExecuteAsync($$"""{"name":"validate.patch","offset":{{offset}}}""");
                pages.Add(page);
                rebuilt.Append(page.AsSpan(page.IndexOf(separator, StringComparison.Ordinal) + separator.Length));
                var cursor = page[(page.IndexOf("nextOffset=", StringComparison.Ordinal) + "nextOffset=".Length)..page.IndexOf(separator, StringComparison.Ordinal)];
                offset = cursor.StartsWith("null", StringComparison.Ordinal) ? null : cursor;
            }
        }

        AssertEx.True(content.Length > WorkSessionToolDefinitions.ReadUpstreamArtifact.PageCharacters, "the artifact must need more than one full page.");
        AssertEx.True(pages.TrueForAll(static page => page.Length <= budget), "every page, header included, fits the tool-result budget.");
        AssertEx.Equal(content, rebuilt.ToString(), "following nextOffset rebuilds the artifact with nothing skipped.");
    }

    /// <summary>What the step was handed is the whole readable set: any other name is refused, and the refusal lists what can be read.</summary>
    [Test]
    public async Task ReadUpstreamArtifact_ReadsOnlyWhatTheStepWasHanded()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var artifact = await ReadableAsync(factory, "validate.patch", "validate", "diff --git a/x b/x");
        var handler = Handler(factory, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName);

        string unknown, nothingHanded;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            using (WorkSessionTurnScope.Begin(readableArtifacts: [artifact]))
            {
                unknown = await handler.ExecuteAsync("""{"name":"secrets.env"}""");
            }

            using (WorkSessionTurnScope.Begin())
            {
                nothingHanded = await handler.ExecuteAsync("""{"name":"validate.patch"}""");
            }
        }

        AssertEx.Contains(unknown, "No upstream artifact handed to this step is named 'secrets.env'");
        AssertEx.Contains(unknown, "\"validate.patch\" (from 'validate')", message: "the refusal names what the step CAN read.");
        AssertEx.Contains(nothingHanded, "handed no upstream artifacts");
        AssertEx.False(nothingHanded.Contains("diff --git", StringComparison.Ordinal), "a step without the set reads nothing, whatever it asks for.");
    }

    /// <summary>Two producers can hand one step the same name, so a bare name is refused naming both, and producer picks one.</summary>
    [Test]
    public async Task ReadUpstreamArtifact_WhenTwoProducersShareAName_AsksWhichAndReadsTheOneNamed()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var left = await ReadableAsync(factory, "report.md", "left", "the left report");
        var right = await ReadableAsync(factory, "report.md", "right", "the right report");
        var handler = Handler(factory, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName);

        string ambiguous, picked;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(readableArtifacts: [left, right]))
        {
            ambiguous = await handler.ExecuteAsync("""{"name":"report.md"}""");
            picked = await handler.ExecuteAsync("""{"name":"report.md","producer":"right"}""");
        }

        AssertEx.Contains(ambiguous, "made by 'left', 'right'");
        AssertEx.False(ambiguous.Contains("the left report", StringComparison.Ordinal), "an ambiguous name reads neither document.");
        AssertEx.Contains(picked, "the right report");
        AssertEx.False(picked.Contains("the left report", StringComparison.Ordinal));
    }

    [Test]
    public async Task ReadUpstreamArtifact_ForAnArtifactThatIsNotText_GivesItsFactsAndNoBytes()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var artifact = await ReadableAsync(factory, "build.zip", "package", "PK-binary", mediaType: "application/zip");
        var handler = Handler(factory, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName);

        string result;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(readableArtifacts: [artifact]))
        {
            result = await handler.ExecuteAsync("""{"name":"build.zip"}""");
        }

        AssertEx.Contains(result, "is application/zip, 9 bytes");
        AssertEx.Contains(result, "not text");
        AssertEx.False(result.Contains("PK-binary", StringComparison.Ordinal));
    }

    /// <summary>Bytes that no longer match the digest the artifact row recorded are a refusal, never content.</summary>
    [Test]
    public async Task ReadUpstreamArtifact_WhenTheStoredBytesDoNotVerify_RefusesAndReadsNothing()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());
        var artifact = await ReadableAsync(factory, "validate.patch", "validate", "the real diff");
        var tampered = new WorkSessionReadableArtifact
        {
            RunId = artifact.RunId,
            ArtifactId = artifact.ArtifactId,
            Name = artifact.Name,
            Kind = artifact.Kind,
            Producer = artifact.Producer,
            Version = artifact.Version,
            MediaType = artifact.MediaType,
            ContentSha256 = new string('0', 64),
            SizeBytes = artifact.SizeBytes
        };
        var handler = Handler(factory, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName);

        string result;
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        using (WorkSessionTurnScope.Begin(readableArtifacts: [tampered]))
        {
            result = await handler.ExecuteAsync("""{"name":"validate.patch"}""");
        }

        AssertEx.Contains(result, "did not verify");
        AssertEx.False(result.Contains("the real diff", StringComparison.Ordinal), "unverified bytes never reach the model.");
    }

    [Test]
    public async Task EveryHandler_WithoutAnAmbientConversation_FailsClosed()
    {
        var factory = Host.Factory;

        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => SessionToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Equal("This tool only works inside a work session.",
                await handler.ExecuteAsync("{}"),
                $"{handler.ToolName} must be inert outside a session — that is what makes the profile-opt-in offer safe.");
        }
    }

    [Test]
    public async Task EveryHandler_OnAConversationWithNoSession_FailsClosed()
    {
        var factory = Host.Factory;

        using var scope = AgentRunConversationContext.BeginScope(Guid.NewGuid());
        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => SessionToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Contains(await handler.ExecuteAsync("{}"), "only works inside a work session");
        }
    }

    [Test]
    public async Task EveryHandler_OnAClosedSession_Refuses()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                           .TransitionStatusAsync(new TransitionWorkSessionStatusCommand
                           {
                               SessionId = sessionId,
                               ExpectedVersion = session.Version,
                               TargetStatus = AgentWorkSessionStatus.Cancelled
                           });
        }

        using var ambient = AgentRunConversationContext.BeginScope(session.ConversationId);
        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => SessionToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Contains(await handler.ExecuteAsync("{}"),
                "already closed",
                message: $"{handler.ToolName} must refuse a closed session before it looks at the arguments.");
        }
    }

    [Test]
    public async Task EveryHandler_WhenTheFeatureIsDisabled_SaysSoRatherThanWriting()
    {
        // The kill switch gates behaviour, never registration: an empty container would answer 500 where a disabled node
        // has to answer legibly. Private host: that kill switch is a host-level config value.
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["WorkSessions:Enabled"] = "false"
            }
        };

        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => SessionToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Equal("Work sessions are disabled on this node.", await handler.ExecuteAsync("{}"));
        }
    }

    [Test]
    public async Task EveryHandler_OnMalformedJson_ReturnsTheParseSentence()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid());

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => SessionToolNames.Contains(candidate.ToolName)))
        {
            var result = await handler.ExecuteAsync("{ not json");
            AssertEx.Contains(result, "were not valid JSON");
            AssertEx.Contains(result,
                Example(handler.ToolName),
                message: $"{handler.ToolName} must hand back a shape the model can copy rather than the parser's message.");
        }
    }

    /// <summary>The intake classifier's contract, reduced to one required enum and one optional string.</summary>
    private static readonly WorkSessionOutputSchema KindContract = new()
    {
        Properties =
        [
            new WorkSessionOutputProperty
            {
                Name = "kind",
                Type = WorkSessionOutputType.String,
                Required = true,
                Enum = ["feature", "bug", "question", "unclear"],
                MaxLength = null
            },
            new WorkSessionOutputProperty
            {
                Name = "rationale",
                Type = WorkSessionOutputType.String,
                Required = false,
                Enum = null,
                MaxLength = 200
            }
        ]
    };

    /// <summary>Every handler that resolves its session from the ambient conversation: the four state tools and the upstream read.</summary>
    private static readonly IReadOnlyList<string> SessionToolNames = [.. WorkSessionToolDefinitions.ToolNames, WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName];

    private static string Example(string toolName) =>
        toolName switch
        {
            WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName => WorkSessionToolDefinitions.ReadUpstreamArtifact.ExampleArguments,
            WorkSessionToolDefinitions.UpdateWorkPlan.ToolName => WorkSessionToolDefinitions.UpdateWorkPlan.ExampleArguments,
            WorkSessionToolDefinitions.RecordFinding.ToolName => WorkSessionToolDefinitions.RecordFinding.ExampleArguments,
            WorkSessionToolDefinitions.SaveArtifact.ToolName => WorkSessionToolDefinitions.SaveArtifact.ExampleArguments,
            _ => WorkSessionToolDefinitions.CompleteWorkSession.ExampleArguments
        };

    private static TestServerWebAppFactory NewFactory(RecordingWorkSessionEventPublisher? publisher = null, params (string Key, string Value)[] configuration) =>
        new()
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(configuration),
            ConfigureAdditionalTestServices = publisher is null
                ? null
                : services =>
                {
                    services.RemoveAll<IWorkSessionEventPublisher>();
                    services.AddSingleton<IWorkSessionEventPublisher>(publisher);
                }
        };

    /// <summary>The completion the tool recorded, parsed the way the supervisor and the workflow executor parse it.</summary>
    private static WorkSessionCompletionDetail? ReadCompletionDetail(IReadOnlyList<WorkSessionEventSnapshot> events)
    {
        var recorded = AssertEx.NotNull(events.LastOrDefault(static entry => entry.EventType == WorkSessionEventTypes.CompletionRequested),
            "The tool records the request as an event.");
        return JsonSerializer.Deserialize<WorkSessionCompletionDetail>(AssertEx.NotNull(recorded.DetailJson, "The event carries the completion detail."));
    }

    /// <summary>The id a model would copy out of an add result: the 36 characters after the task's quoted title and ' = '.</summary>
    private static string IdFromAddResult(string result, string title)
    {
        var marker = $"\"{title}\" = ";
        var start = result.IndexOf(marker, StringComparison.Ordinal);
        AssertEx.True(start >= 0, $"The add result names '{title}'. Result: {result}");
        return result.Substring(start + marker.Length, 36);
    }

    /// <summary>Writes the bytes through the real workflow blob store and answers the readable entry a workflow step would carry for them.</summary>
    private static async Task<WorkSessionReadableArtifact> ReadableAsync(TestServerWebAppFactory factory, string name, string producer, string content, string mediaType = "text/x-diff")
    {
        var runId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var written = await factory.Services.GetRequiredService<IDevWorkflowArtifactBlobStore>().WriteAsync(runId, artifactId, System.Text.Encoding.UTF8.GetBytes(content));
        return new WorkSessionReadableArtifact
        {
            RunId = runId,
            ArtifactId = artifactId,
            Name = name,
            Kind = "Patch",
            Producer = producer,
            Version = 1,
            MediaType = mediaType,
            ContentSha256 = written.ContentHash,
            SizeBytes = written.ByteCount
        };
    }

    private static IClientLocalToolHandler Handler(TestServerWebAppFactory factory, string toolName) =>
        AssertEx.NotNull(factory.Services.GetServices<IClientLocalToolHandler>().SingleOrDefault(handler => handler.ToolName == toolName),
            $"{toolName} must be registered exactly once.");

    private static async Task<Guid?> ReadDetailCurrentTaskAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().GetAsync(sessionId)).CurrentTaskId;
    }

    private static async Task<IReadOnlyList<WorkSessionTaskSnapshot>> ReadTasksAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListTasksAsync(sessionId);
    }
}
