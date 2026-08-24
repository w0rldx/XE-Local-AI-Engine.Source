namespace XE_Local_AI_Engine.Tests.WorkSessions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The four state tools against the real store. The session is resolved from the ambient conversation id the
///     invocation runner seeds, so every case here stages that scope — or deliberately does not, which is the
///     fail-closed path that makes the profile-opt-in offer safe.
/// </summary>
public sealed class WorkSessionToolHandlerTests
{
    [Test]
    public async Task UpdateWorkPlan_AddsAndCompletesTasks()
    {
        await using var factory = NewFactory();
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
    public async Task UpdateWorkPlan_WhenAnOperationIsUnknown_ReturnsAnActionableSentence()
    {
        await using var factory = NewFactory();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.UpdateWorkPlan.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        var result = await handler.ExecuteAsync("""{"operations":[{"op":"annihilate"}]}""").ConfigureAwait(false);

        AssertEx.Contains(result, "must be one of add, update, complete or drop");
    }

    [Test]
    public async Task UpdateWorkPlan_WhenATitleIsOverLength_ReturnsTheBoundSentence()
    {
        await using var factory = NewFactory();
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
        await using var factory = NewFactory();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);
        var handler = Handler(factory, WorkSessionToolDefinitions.RecordFinding.ToolName);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        AssertEx.Contains(await handler.ExecuteAsync("""{"kind":"Rumour","text":"Something."}""").ConfigureAwait(false),
            "must be one of Finding, Evidence, Decision or OpenQuestion");
    }

    [Test]
    public async Task SaveArtifact_WritesTheBlobThenTheRow_AndReplacesByName()
    {
        await using var factory = NewFactory();
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
        await using var factory = NewFactory();
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
        await using var factory = NewFactory();
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

    [Test]
    public async Task EveryHandler_WithoutAnAmbientConversation_FailsClosed()
    {
        await using var factory = NewFactory();

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
        await using var factory = NewFactory();

        using var scope = AgentRunConversationContext.BeginScope(Guid.NewGuid());
        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => WorkSessionToolDefinitions.ToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Contains(await handler.ExecuteAsync("{}").ConfigureAwait(false), "only works inside a work session");
        }
    }

    [Test]
    public async Task EveryHandler_OnAClosedSession_Refuses()
    {
        await using var factory = NewFactory();
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
        // has to answer legibly.
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
        await using var factory = NewFactory();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, Guid.NewGuid()).ConfigureAwait(false);

        using var scope = AgentRunConversationContext.BeginScope(session.ConversationId);
        foreach (var handler in factory.Services.GetServices<IClientLocalToolHandler>().Where(candidate => WorkSessionToolDefinitions.ToolNames.Contains(candidate.ToolName)))
        {
            AssertEx.Contains(await handler.ExecuteAsync("{ not json").ConfigureAwait(false), "were not valid JSON");
        }
    }

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

    private static IClientLocalToolHandler Handler(TestServerWebAppFactory factory, string toolName) =>
        AssertEx.NotNull(factory.Services.GetServices<IClientLocalToolHandler>().SingleOrDefault(handler => handler.ToolName == toolName),
            $"{toolName} must be registered exactly once.");

    private static async Task<IReadOnlyList<WorkSessionTaskSnapshot>> ReadTasksAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().ListTasksAsync(sessionId).ConfigureAwait(false);
    }
}
