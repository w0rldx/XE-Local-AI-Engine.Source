namespace XE_Local_AI_Engine.Tests.WorkSessions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The contract the REST layer sits on: which agent a session may run on, when its objective may change, what a
///     delete takes with it, and what a follow-up does to a paused run.
/// </summary>
public sealed class WorkSessionServiceTests
{
    [ClassDataSource<WorkSessionServiceHostFixture>(Shared = SharedType.PerClass)]
    public required WorkSessionServiceHostFixture Host { get; init; }

    [Test]
    public async Task Create_MintsTheOwnedConversationAndReturnsTheEffectiveStepBudget()
    {
        // Private host: the step budget it asserts on IS a host-level config override.
        await using var factory = NewFactory(("WorkSessions:MaxStepsPerRun", "9"));
        var agentId = await SeedAgentAsync(factory, "tool-capable-model").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();
        var created = await service.CreateAsync(new CreateWorkSessionRequestModel("Runtime research", "Explain the inference path.", AgentWorkSessionKind.Research, agentId))
                                   .ConfigureAwait(false);

        AssertEx.Equal(AgentWorkSessionStatus.Draft, created.Status);
        AssertEx.Equal(expected: 9, created.MaxStepsPerRun, "The step budget is the node's effective option, so the page can render 'step N of M'.");
        AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>().GetConversationAsync(created.ConversationId).ConfigureAwait(false),
            "A session owns a real conversation from the moment it exists.");
        AssertEx.Contains(await service.ListAsync().ConfigureAwait(false), summary => summary.Id == created.Id);
    }

    [Test]
    public async Task Create_StampsTheOwnedConversationAsAWorkSessionAndKeepsItOutOfTheChatList()
    {
        var agentId = await SeedAgentAsync(Host.Factory, "tool-capable-model").ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();
        var chat = scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>();

        var created = await service.CreateAsync(new CreateWorkSessionRequestModel("Kind check", "Prove the discriminator.", AgentWorkSessionKind.Research, agentId))
                                   .ConfigureAwait(false);

        AssertEx.NotNull(await chat.GetConversationAsync(created.ConversationId).ConfigureAwait(false),
            "A by-id read stays unfiltered — the session's own transcript reader depends on it.");

        var listed = (await chat.ListConversationsAsync(new NodeChatListConversationsRequest(IncludeArchived: true)).ConfigureAwait(false))
                     .Select(static summary => summary.ConversationId)
                     .ToArray();
        AssertEx.False(listed.Contains(created.ConversationId),
            "WorkSessionService must create its conversation with NodeConversationKind.WorkSession, so it never shows as a chat the operator did not start.");
    }

    /// <summary>
    ///     FU-5: the caller's own model pin BEATS the bound agent's, and is what the tool gate judges — in both
    ///     directions, because an override that only ever loosened or only ever tightened would not be a pin.
    /// </summary>
    [Test]
    public async Task Create_WithAModelOverride_JudgesTheOverrideRatherThanTheAgentsOwnPin()
    {
        var factory = Host.Factory;
        var listed = await SeedAgentAsync(factory, "tool-capable-model").ConfigureAwait(false);
        var unlisted = await SeedAgentAsync(factory, "a-model-nobody-listed").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();

        var rejection = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() =>
                                          service.CreateAsync(new CreateWorkSessionRequestModel("t",
                                              "o",
                                              AgentWorkSessionKind.General,
                                              listed,
                                              new WorkSessionRuntimeOverride("a-model-nobody-listed", ReasoningEffort: null))))
                                      .ConfigureAwait(false);

        // The refusal names the model the session would have run on, not the agent's own.
        AssertEx.Contains(rejection.Message, "a-model-nobody-listed");

        var created = await service.CreateAsync(new CreateWorkSessionRequestModel("t",
                                       "o",
                                       AgentWorkSessionKind.General,
                                       unlisted,
                                       new WorkSessionRuntimeOverride("tool-capable-model", "high")))
                                   .ConfigureAwait(false);

        AssertEx.Equal(AgentWorkSessionStatus.Draft, created.Status, "an override onto a listed model admits a session the agent's own pin would have been refused for.");
    }

    [Test]
    public async Task Create_WhenTheAgentIsUnknown_IsRejected()
    {
        var factory = Host.Factory;
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();

        var rejection = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() =>
                                          service.CreateAsync(new CreateWorkSessionRequestModel("t", "o", AgentWorkSessionKind.General, Guid.NewGuid())))
                                      .ConfigureAwait(false);

        AssertEx.Contains(rejection.Message, "could not be found");
    }

    [Test]
    public async Task Create_WhenTheAgentsModelCannotCallTools_IsRejected()
    {
        // A session on a non-tool-capable model can never call a state tool: it would run its whole step budget writing
        // nothing at all.
        var factory = Host.Factory;
        var agentId = await SeedAgentAsync(factory, "a-model-without-tools").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var rejection = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() =>
                                          scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                               .CreateAsync(new CreateWorkSessionRequestModel("t", "o", AgentWorkSessionKind.General, agentId)))
                                      .ConfigureAwait(false);

        AssertEx.Contains(rejection.Message, "cannot call tools");
    }

    [Test]
    public async Task Create_WhenTheAgentsModelIsOutsideTheNodesToolCapableList_IsRejected()
    {
        // The SECOND tool gate, and the one that used to be missed here: the model's own capability probe says yes
        // while the operator's allow-list — which the offer applies unconditionally, cloud pins included — says no. The
        // session would be created, every state-tool call would come back "Requested function ... not found", and the
        // run would spend its whole step budget writing nothing.
        var factory = Host.Factory;
        var agentId = await SeedAgentAsync(factory, "a-model-nobody-listed").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var rejection = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() =>
                                          scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                               .CreateAsync(new CreateWorkSessionRequestModel("t", "o", AgentWorkSessionKind.General, agentId)))
                                      .ConfigureAwait(false);

        AssertEx.Contains(rejection.Message, "tool-capable model list");
        // The operator has to be told WHICH model to add, not merely that one is missing.
        AssertEx.Contains(rejection.Message, "a-model-nobody-listed");
        AssertEx.False(rejection.Message.Contains("cannot call tools", StringComparison.Ordinal),
            "The two gates have different fixes, so they must not share a message.");

        // Scoped to this test's own agent rather than asserting an empty list: the host — and so the session table — is
        // shared with every sibling in this class.
        AssertEx.False((await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().ListAsync().ConfigureAwait(false))
            .Any(summary => summary.AgentDefinitionId == agentId),
            "The refused create persisted no session.");
    }

    [Test]
    public async Task Update_RepointingAtAnAgentOutsideTheToolCapableList_IsRejected_ButAListedOneIsAccepted()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var seeded = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var unlistedAgentId = await SeedAgentAsync(factory, "a-model-nobody-listed").ConfigureAwait(false);
        var listedAgentId = await SeedAgentAsync(factory, "another-local-model").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();
        var rejection = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() =>
                                          service.UpdateAsync(sessionId, new UpdateWorkSessionRequestModel(null, null, unlistedAgentId)))
                                      .ConfigureAwait(false);

        AssertEx.Contains(rejection.Message, "tool-capable model list");
        AssertEx.Equal(seeded.AgentDefinitionId,
            (await service.GetAsync(sessionId).ConfigureAwait(false)).AgentDefinitionId,
            "The refused repoint left the session on the agent it had.");

        var repointed = await service.UpdateAsync(sessionId, new UpdateWorkSessionRequestModel(null, null, listedAgentId)).ConfigureAwait(false);
        AssertEx.Equal(listedAgentId, repointed.AgentDefinitionId, "A listed model still repoints.");
    }

    [Test]
    public async Task Create_WhenTheKindIsDevelopment_IsRejected()
    {
        var factory = Host.Factory;
        var agentId = await SeedAgentAsync(factory, "tool-capable-model").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        _ = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() =>
                              scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                   .CreateAsync(new CreateWorkSessionRequestModel("t", "o", AgentWorkSessionKind.Development, agentId)))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task EveryReader_OnAnUnknownSession_ThrowsWorkSessionNotFound()
    {
        var factory = Host.Factory;
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();
        var unknown = Guid.NewGuid();

        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.GetAsync(unknown)).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.UpdateAsync(unknown, new UpdateWorkSessionRequestModel("t", null, null))).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.DeleteAsync(unknown)).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.StartAsync(unknown)).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.PostFollowUpAsync(unknown, "hello")).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.ReadArtifactContentAsync(unknown, Guid.NewGuid())).ConfigureAwait(false);
    }

    [Test]
    public async Task Update_WhileTheSessionIsRunning_RefusesTheObjective_ButNotTheTitle()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        _ = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, session.Version, AgentWorkSessionStatus.Running)).ConfigureAwait(false);

        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();
        _ = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() =>
                              service.UpdateAsync(sessionId, new UpdateWorkSessionRequestModel(null, "A different objective", null)))
                          .ConfigureAwait(false);

        var renamed = await service.UpdateAsync(sessionId, new UpdateWorkSessionRequestModel("Renamed mid-run", null, null)).ConfigureAwait(false);
        AssertEx.Equal("Renamed mid-run", renamed.Title, "A title never changes what the running step sees.");
    }

    [Test]
    public async Task Resume_OnARunningSession_IsRefused()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, session.Version, AgentWorkSessionStatus.Running))
                       .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() =>
                              scope.ServiceProvider.GetRequiredService<IWorkSessionService>().ResumeAsync(sessionId))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task Delete_WhileTheSessionIsRunning_IsRefused()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, session.Version, AgentWorkSessionStatus.Running))
                       .ConfigureAwait(false);

        var refusal = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() =>
                                        scope.ServiceProvider.GetRequiredService<IWorkSessionService>().DeleteAsync(sessionId))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "Cancel the work session before deleting it");
    }

    [Test]
    public async Task Delete_TakesTheOwnedConversationWithIt()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().DeleteAsync(sessionId).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>().GetAsync(sessionId))
                          .ConfigureAwait(false);
        var conversation = await scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>().GetConversationAsync(session.ConversationId).ConfigureAwait(false);
        AssertEx.True(conversation is null || conversation.Purged, "A session's conversation exists only to carry it, so nothing may orphan.");
    }

    [Test]
    public async Task PostFollowUp_OverTheNodesMessageCap_ThrowsAndPersistsNothing()
    {
        // The cap lives in the chat hub, which a REST follow-up never passes through — and the row is persisted before
        // anything downstream could inspect it.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var cap = scope.ServiceProvider.GetRequiredService<IOptions<SecurityOptions>>().Value.MaxMessageSizeKb;
        var refusal = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() =>
                                        scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                             .PostFollowUpAsync(sessionId, new string('x', (cap * 1024) + 1)))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "too large");
        var conversation = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>()
                                                       .GetConversationAsync(session.ConversationId)
                                                       .ConfigureAwait(false),
            "The conversation still exists.");
        AssertEx.Empty(conversation.Messages);
    }

    [Test]
    public async Task PostFollowUp_OnAPausedSession_PersistsTheTurnAndAsksForAStep()
    {
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        var sessionId = Guid.NewGuid();
        // Private host: it replaces the stream service and the event publisher with per-test recording fakes.
        await using var factory = NewFactory(configureExtra: services =>
            {
                services.RemoveAll<INodeChatStreamService>();
                services.AddSingleton<INodeChatStreamService>(provider =>
                    stream = new FakeNodeChatStreamService(provider.GetRequiredService<INodeChatStreamCancellationRegistry>(), provider, sessionId));
                services.RemoveAll<IWorkSessionEventPublisher>();
                services.AddSingleton<IWorkSessionEventPublisher>(publisher);
            },
            configuration: ("WorkSessions:MaxStepsPerRun", "1"));

        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await PauseAsync(factory, sessionId).ConfigureAwait(false);
        _ = factory.Services.GetRequiredService<INodeChatStreamService>();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().PostFollowUpAsync(sessionId, "Also check the ADR.").ConfigureAwait(false);
        }

        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);
        AssertEx.NotNull(stream, "The auto-resume must have driven a step.");
        AssertEx.NotEmpty(stream!.Requests, "A paused session picks a follow-up up by resuming, which is what the composer implies.");

        await using var read = factory.Services.CreateAsyncScope();
        var conversation = AssertEx.NotNull(await read.ServiceProvider.GetRequiredService<INodeChatPersistenceService>()
                                                      .GetConversationAsync(session.ConversationId)
                                                      .ConfigureAwait(false),
            "The conversation carries the follow-up.");
        AssertEx.Contains(conversation.Messages, message => message.Content == "Also check the ADR.");
    }

    [Test]
    public async Task PostFollowUp_WhileTheSessionIsParked_PersistsTheTurnButDoesNotResume()
    {
        // A parked step already owns the node's one invocation slot and its prompt is answered through the chat card, so
        // a follow-up queues for the next step rather than starting one.
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var running = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, session.Version, AgentWorkSessionStatus.Running))
                                 .ConfigureAwait(false);
        _ = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, running.Version, AgentWorkSessionStatus.WaitingForInput)).ConfigureAwait(false);

        _ = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().PostFollowUpAsync(sessionId, "One more thing.").ConfigureAwait(false);

        AssertEx.Equal(AgentWorkSessionStatus.WaitingForInput, (await store.GetAsync(sessionId).ConfigureAwait(false)).Status);
    }

    [Test]
    public async Task ReadArtifactContent_ForAnUnknownOrInvalidArtifact_ThrowsWorkSessionNotFound()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.ReadArtifactContentAsync(sessionId, Guid.NewGuid())).ConfigureAwait(false);

        // A row whose bytes were never written: the blob read fails, and the node refuses to hand over content it cannot
        // vouch for rather than returning something plausible.
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var artifactId = Guid.NewGuid();
        _ = await store.AppendArtifactAsync(new AppendWorkSessionArtifactCommand(sessionId,
                           artifactId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionArtifactKind.Note,
                           "phantom.txt",
                           "text/plain",
                           new string('a', 64),
                           SizeBytes: 4,
                           "work-session-artifact:phantom"))
                       .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.ReadArtifactContentAsync(sessionId, artifactId)).ConfigureAwait(false);
    }

    [Test]
    public async Task GetArtifact_ReturnsTheRowWithoutOpeningTheBytes_AndRefusesAForeignSession()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var otherSessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, otherSessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var artifactId = Guid.NewGuid();
        _ = await store.AppendArtifactAsync(new AppendWorkSessionArtifactCommand(sessionId,
                           artifactId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionArtifactKind.Report,
                           "report.md",
                           "text/markdown",
                           new string('b', 64),
                           SizeBytes: 12,
                           "work-session-artifact:report"))
                       .ConfigureAwait(false);

        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();

        // The row alone — the bytes were never written, and the metadata read must not need them (this is what the
        // content endpoint's size ceiling is checked against, before any blob is opened).
        var artifact = await service.GetArtifactAsync(sessionId, artifactId).ConfigureAwait(false);
        AssertEx.Equal("report.md", artifact.Name);
        AssertEx.Equal(expected: 12L, artifact.SizeBytes);

        // Asked through another session's route, the artifact reads as absent — an id is not an authorization.
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.GetArtifactAsync(otherSessionId, artifactId)).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<WorkSessionNotFoundException>(() => service.GetArtifactAsync(sessionId, Guid.NewGuid())).ConfigureAwait(false);
    }

    [Test]
    public async Task ListEvents_ClampsTheRequestedPageSize_AndCarriesTheOperationId()
    {
        var factory = Host.Factory;
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var operationId = Guid.NewGuid();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .AppendEventAsync(new AppendWorkSessionEventCommand(sessionId, WorkSessionVersions.Any, "tool.completed", operationId))
                       .ConfigureAwait(false);

        var events = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>().ListEventsAsync(sessionId, sinceSequence: 0, limit: 100_000).ConfigureAwait(false);

        AssertEx.True(events.Count <= 500, "A caller cannot ask the node for an unbounded page.");

        // The operation id the store records is the one a client groups a step's rows by, so the DTO must carry it.
        var recorded = events.Single(entry => entry.EventType == "tool.completed");
        AssertEx.Equal(operationId, recorded.OperationId);
    }

    [Test]
    public async Task LifecycleVerbs_WhenTheFeatureIsDisabled_SaySoRatherThanFailingOpaquely()
    {
        // Private host: the kill switch it asserts on is a host-level config value.
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["WorkSessions:Enabled"] = "false"
            }
        };
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();
        var refusal = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() => service.StartAsync(sessionId)).ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "disabled on this node");
        AssertEx.NotNull(await service.GetAsync(sessionId).ConfigureAwait(false), "Reads keep working so the page can explain itself.");
    }

    internal static TestServerWebAppFactory NewFactory(params (string Key, string Value)[] configuration) =>
        NewFactory(configureExtra: null, configuration);

    internal static TestServerWebAppFactory NewFactory(Action<IServiceCollection>? configureExtra, params (string Key, string Value)[] configuration) =>
        new()
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(configuration),
            ConfigureAdditionalTestServices = services =>
            {
                // Deterministic capability answers: the real resolver probes providers, which would make these
                // assertions depend on what happens to be installed.
                services.RemoveAll<IModelCapabilityResolver>();
                services.AddSingleton<IModelCapabilityResolver, StubModelCapabilityResolver>();
                services.RemoveAll<ILocalDefaultChatModelResolver>();
                services.AddSingleton<ILocalDefaultChatModelResolver, StubDefaultChatModelResolver>();
                configureExtra?.Invoke(services);
            }
        };

    internal static async Task<Guid> SeedAgentAsync(TestServerWebAppFactory factory, string modelProfile)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        // GUID-suffixed: the classes that call this share one host, so a fixed name would make the definition table
        // unfilterable for a test that wants only its own agent.
        var definition = await store.AddAsync(new AgentDefinitionInput($"Agent on {modelProfile} {Guid.NewGuid():N}",
                                        Description: null,
                                        "Work the objective.",
                                        modelProfile,
                                        ReasoningEffort: null,
                                        AgentDefinitionKind.Single,
                                        [],
                                        new Dictionary<string, bool>(StringComparer.Ordinal),
                                        OrchestrationTopologyJson: null))
                                    .ConfigureAwait(false);
        return definition.Id;
    }

    private static async Task PauseAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        var session = await store.GetAsync(sessionId).ConfigureAwait(false);
        var running = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, session.Version, AgentWorkSessionStatus.Running))
                                 .ConfigureAwait(false);
        _ = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, running.Version, AgentWorkSessionStatus.Paused)).ConfigureAwait(false);
    }

    /// <summary>Tool-capable unless the model's name says otherwise; cloud only for the explicitly cloud-named ones.</summary>
    internal sealed class StubModelCapabilityResolver : IModelCapabilityResolver
    {
        public Task<ModelCapabilitySnapshot> ResolveAsync(string? model, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: false,
                model?.Contains("without-tools", StringComparison.Ordinal) != true,
                model?.Contains("cloud", StringComparison.Ordinal) == true));
    }

    internal sealed class StubDefaultChatModelResolver : ILocalDefaultChatModelResolver
    {
        public Task<string?> ResolveAsync(string? persistedDefault, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("tool-capable-model");
    }
}
