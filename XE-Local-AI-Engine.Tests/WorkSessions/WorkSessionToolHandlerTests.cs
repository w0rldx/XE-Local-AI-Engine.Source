namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The four state tools against the real store. The session is resolved from the ambient conversation id the
///     invocation runner seeds, so every case here stages that scope — or deliberately does not, which is the
///     fail-closed path that makes the profile-opt-in offer safe.
/// </summary>
public sealed class WorkSessionToolHandlerTests
{
    [ClassDataSource<WorkSessionHostFixture>(Shared = SharedType.PerClass)]
    public required WorkSessionHostFixture Host { get; init; }

    [Test]
    public async Task UpdateWorkPlan_AddsAndCompletesTasks()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            var added = await handler.ExecuteAsync("""{"operations":[{"op":"add","title":"Read the runtime docs","status":"Active"}]}""").ConfigureAwait(false);
            AssertEx.Contains(added, "1 work-plan change");
        }

        var tasks = await ReadTasksAsync(factory, sessionId).ConfigureAwait(false);
        var task = AssertEx.NotNull(tasks.SingleOrDefault(), "The batch adds exactly one task.");
        AssertEx.Equal("Read the runtime docs", task.Title);
        AssertEx.Equal(AgentWorkSessionTaskStatus.Active, task.Status);
        AssertEx.Equal(AgentWorkSessionTaskOrigin.Agent, task.Origin, "A tool-authored task is the agent's, never the user's.");

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync($$"""{"operations":[{"op":"complete","taskId":"{{task.Id}}"}]}""").ConfigureAwait(false);
        }

        AssertEx.Equal(AgentWorkSessionTaskStatus.Done, (await ReadTasksAsync(factory, sessionId).ConfigureAwait(false)).Single().Status);
    }

    [Test]
    public async Task UpdateWorkPlan_MovesTheSessionsCurrentTaskPointer_SoTheDetailReadIsNotStale()
    {
        // The pointer used to move only on a status transition, so it went stale for the rest of a multi-step run every
        // time the agent switched tasks — and WorkSessionDetail.CurrentTaskId is what the session page reads.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync("""{"operations":[{"op":"add","title":"First","status":"Active"},{"op":"add","title":"Second"}]}""").ConfigureAwait(false);
        }

        var tasks = await ReadTasksAsync(factory, sessionId).ConfigureAwait(false);
        var first = tasks.Single(task => task.Title == "First");
        var second = tasks.Single(task => task.Title == "Second");
        AssertEx.Equal(first.Id, await ReadDetailCurrentTaskAsync(factory, sessionId).ConfigureAwait(false));

        // Switching the active task mid-run must move the pointer with it.
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync($$"""{"operations":[{"op":"complete","taskId":"{{first.Id}}"},{"op":"update","taskId":"{{second.Id}}","status":"Active"}]}""")
                             .ConfigureAwait(false);
        }

        AssertEx.Equal(second.Id, await ReadDetailCurrentTaskAsync(factory, sessionId).ConfigureAwait(false));

        // Finishing the current task leaves no pointer rather than one aimed at finished work.
        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync($$"""{"operations":[{"op":"complete","taskId":"{{second.Id}}"}]}""").ConfigureAwait(false);
        }

        AssertEx.Null(await ReadDetailCurrentTaskAsync(factory, sessionId).ConfigureAwait(false));
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
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync($$"""{"operations":[{"op":"add","{{alias}}":" Read the runtime docs "}]}""").ConfigureAwait(false),
                "1 work-plan change");
        }

        AssertEx.Equal("Read the runtime docs",
            (await ReadTasksAsync(factory, sessionId).ConfigureAwait(false)).Single().Title,
            $"'{alias}' is an alias for 'title', trimmed like one.");
    }

    [Test]
    public async Task UpdateWorkPlan_Add_WhenNoTitleOrAlias_ReturnsSentenceWithExample()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"operations":[{"op":"add"}]}""").ConfigureAwait(false);

        AssertEx.Contains(result, "needs a title");
        AssertEx.Contains(result, "\"op\":\"add\"", message: "The sentence carries a shape the model can copy, not just a complaint.");
    }

    [Test]
    public async Task UpdateWorkPlan_WhenAKeyIsUnknown_ReturnsTheExampleAndNoClrTypeName()
    {
        // Valid JSON, wrong shape: the deserializer rejects the unknown key for the whole batch, and its own message
        // names the CLR request type — useless to a model and the thing that used to be echoed straight back.
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"operations":[{"op":"add","label":"Investigate"}]}""").ConfigureAwait(false);

        AssertEx.Contains(result, WorkSessionToolDefinitions.UpdateWorkPlan.ExampleArguments);
        AssertEx.False(result.Contains("WorkPlanOperationRequest", StringComparison.Ordinal), "The parser's message is for the log, never for the model.");
    }

    [Test]
    public async Task UpdateWorkPlan_WhenAnOperationIsUnknown_ReturnsAnActionableSentence()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"operations":[{"op":"annihilate"}]}""").ConfigureAwait(false);

        AssertEx.Contains(result, "must be one of add, update, complete or drop");
    }

    [Test]
    public async Task UpdateWorkPlan_WhenATitleIsOverLength_ReturnsTheBoundSentence()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync($$"""{"operations":[{"op":"add","title":"{{new string('t', 400)}}"}]}""").ConfigureAwait(false);

        AssertEx.Contains(result, "exceeded the maximum length");
    }

    [Test]
    public async Task RecordFinding_WritesTheRowAndPublishesItsWatermark()
    {
        var publisher = new RecordingWorkSessionEventPublisher();
        // Private host: the recording publisher is per-test state, so sharing it would let siblings' publishes bleed in.
        await using var factory = NewFactory(publisher);
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.RecordFinding.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"kind":"Decision","text":"Use the process sandbox.","sourceRef":"docs/adr-0004"}""").ConfigureAwait(false);

        AssertEx.Contains(result, "Recorded a Decision");
        var finding = (await WorkSessionTestSupport.ReadFindingsAsync(factory.Services, sessionId).ConfigureAwait(false)).Single();
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
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.RecordFinding.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        AssertEx.Contains(await handler.ExecuteAsync("""{"kind":"Rumour","text":"Something."}""").ConfigureAwait(false),
            "must be one of Finding, Evidence, Decision or OpenQuestion");
    }

    [Test]
    public async Task SaveArtifact_WritesTheBlobThenTheRow_AndReplacesByName()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.SaveArtifact.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"name":"report.md","mediaType":"text/markdown","kind":"Report","text":"first"}""").ConfigureAwait(false),
                "Saved artifact 'report.md'");
            _ = await handler.ExecuteAsync("""{"name":"report.md","mediaType":"text/markdown","kind":"Report","text":"second"}""").ConfigureAwait(false);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var artifacts = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListArtifactsAsync(sessionId).ConfigureAwait(false);
        var artifact = AssertEx.NotNull(artifacts.SingleOrDefault(), "Saving under an existing name replaces it rather than adding a second row.");
        AssertEx.Equal(expected: 6, artifact.SizeBytes);

        var content = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().ReadArtifactContentAsync(sessionId, artifact.Id).ConfigureAwait(false);
        AssertEx.Equal("second", content.Content);
        AssertEx.False(content.IsBase64, "A text media type comes back as text.");
    }

    [Test]
    public async Task SaveArtifact_WhenBothOrNeitherPayloadIsGiven_Refuses()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.SaveArtifact.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        AssertEx.Contains(await handler.ExecuteAsync("""{"name":"a","mediaType":"text/plain","kind":"Note"}""").ConfigureAwait(false), "exactly one of 'text' or 'base64'");
        AssertEx.Contains(await handler.ExecuteAsync("""{"name":"a","mediaType":"text/plain","kind":"Note","text":"x","base64":"eA=="}""").ConfigureAwait(false),
            "exactly one of 'text' or 'base64'");
    }

    [Test]
    public async Task SaveArtifact_WhenTheContentIsOverTheNodeCap_RefusesWithoutWritingARow()
    {
        // Private host: the artifact cap it asserts on is a host-level config value.
        await using var factory = NewFactory(configuration: ("WorkSessions:MaxArtifactBytes", "32"));
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.SaveArtifact.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync($$"""{"name":"big","mediaType":"text/plain","kind":"Note","text":"{{new string('x', 64)}}"}""").ConfigureAwait(false),
                "over this node's");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        AssertEx.Empty(await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListArtifactsAsync(sessionId).ConfigureAwait(false));
    }

    [Test]
    public async Task CompleteWorkSession_RecordsTheRequestWithoutTerminalizingTheSession()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"summary":"Everything asked for is recorded."}""").ConfigureAwait(false), "close at the end of this turn");
        }

        AssertEx.Equal(AgentWorkSessionStatus.Draft,
            (await WorkSessionTestSupport.ReadSessionAsync(factory.Services, sessionId).ConfigureAwait(false)).Status,
            "The tool never terminalizes: the turn has to finish cleanly and the supervisor closes the session.");
        AssertEx.Contains(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false),
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
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync($$"""{"summary":"What it came to.","objectiveMet":{{(objectiveMet ? "true" : "false")}}}""").ConfigureAwait(false);
        }

        AssertEx.Equal(objectiveMet,
            AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false))).ObjectiveMet,
            "The declaration is the whole point of the argument, so it has to survive to the event.");
    }

    [Test]
    public async Task CompleteWorkSession_WithoutObjectiveMet_DeclaresNothing()
    {
        // Absent is not "not met": every completion recorded before the argument existed is one of these, and reading
        // them as unmet would retroactively block node runs whose sessions finished cleanly.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            _ = await handler.ExecuteAsync("""{"summary":"Everything asked for is recorded."}""").ConfigureAwait(false);
        }

        AssertEx.Null(AssertEx.NotNull(ReadCompletionDetail(await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false))).ObjectiveMet);
    }

    [Test]
    public async Task CompleteWorkSession_WhenAKeyIsUnknown_StillRejectsTheWholeCall()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.CompleteWorkSession.ToolName);

        using (AgentRunConversationContext.BeginScope(session.ConversationId))
        {
            AssertEx.Contains(await handler.ExecuteAsync("""{"summary":"Done.","objectiveWasMet":false}""").ConfigureAwait(false),
                WorkSessionToolDefinitions.CompleteWorkSession.ExampleArguments);
        }

        AssertEx.False((await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false))
                       .Any(static entry => entry.EventType == WorkSessionEventTypes.CompletionRequested),
            "A call the deserializer refused must not close the session.");
    }

    [Test]
    public async Task EveryHandler_WithoutAnAmbientConversation_FailsClosed()
    {
        var factory = Host.Factory;

        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => WorkSessionToolDefinitions.ToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Equal("This tool only works inside a work session.",
                await handler.ExecuteAsync("{}").ConfigureAwait(false),
                $"{handler.ToolName} must be inert outside a session — that is what makes the profile-opt-in offer safe.");
        }
    }

    [Test]
    public async Task EveryHandler_OnAConversationWithNoSession_FailsClosed()
    {
        var factory = Host.Factory;

        using var scope = AgentRunConversationContext.BeginScope(Guid.NewGuid());
        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => WorkSessionToolDefinitions.ToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Contains(await handler.ExecuteAsync("{}").ConfigureAwait(false), "only works inside a work session");
        }
    }

    [Test]
    public async Task EveryHandler_OnAClosedSession_Refuses()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                           .TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, session.Version, AgentWorkSessionStatus.Cancelled))
                           .ConfigureAwait(false);
        }

        using var ambient = AgentRunConversationContext.BeginScope(session.ConversationId);
        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => WorkSessionToolDefinitions.ToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Contains(await handler.ExecuteAsync("{}").ConfigureAwait(false),
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

        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => WorkSessionToolDefinitions.ToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Equal("Work sessions are disabled on this node.", await handler.ExecuteAsync("{}").ConfigureAwait(false));
        }
    }

    [Test]
    public async Task EveryHandler_OnMalformedJson_ReturnsTheParseSentence()
    {
        var factory = Host.Factory;
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => WorkSessionToolDefinitions.ToolNames.Contains(candidate.ToolName)))
        {
            var result = await handler.ExecuteAsync("{ not json").ConfigureAwait(false);
            AssertEx.Contains(result, "were not valid JSON");
            AssertEx.Contains(result,
                Example(handler.ToolName),
                message: $"{handler.ToolName} must hand back a shape the model can copy rather than the parser's message.");
        }
    }

    private static string Example(string toolName) =>
        toolName switch
        {
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

    private static IClientLocalToolHandler Handler(TestServerWebAppFactory factory, string toolName) =>
        AssertEx.NotNull(factory.Services.GetServices<IClientLocalToolHandler>().SingleOrDefault(handler => handler.ToolName == toolName),
            $"{toolName} must be registered exactly once.");

    private static async Task<Guid?> ReadDetailCurrentTaskAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().GetAsync(sessionId).ConfigureAwait(false)).CurrentTaskId;
    }

    private static async Task<IReadOnlyList<WorkSessionTaskSnapshot>> ReadTasksAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListTasksAsync(sessionId).ConfigureAwait(false);
    }
}
