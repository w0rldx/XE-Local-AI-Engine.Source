namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.WorkSessions;

/// <summary>
///     Who may move a workflow-owned work session. The run owns its sessions outright, so the ordinary Work Sessions
///     surface refuses all five lifecycle verbs on one and the run drives it through
///     <see cref="IWorkflowOwnedWorkSessionLifecycle" /> instead.
/// </summary>
public sealed class DevWorkflowOwnedWorkSessionTests
{
    /// <summary>
    ///     The Phase 0 gate: a <c>Workflow</c> session is admitted at create — it is a deny-list on <c>Development</c>,
    ///     not an allow-list — and runs its scripted steps to <c>Completed</c> through the owner surface.
    /// </summary>
    [Test]
    public async Task WorkflowSession_CreatesThroughTheServiceAndCompletesThroughTheOwnerSurface()
    {
        FakeNodeChatStreamService? stream = null;
        var publisher = new RecordingWorkSessionEventPublisher();
        await using var factory = WorkSessionServiceTests.NewFactory(services => WorkSessionTestSupport.WithFakes(
            provider => stream = new FakeNodeChatStreamService(provider.GetRequiredService<INodeChatStreamCancellationRegistry>(), provider, Guid.Empty),
            publisher)(services));

        var agentId = await WorkSessionServiceTests.SeedAgentAsync(factory, "tool-capable-model").ConfigureAwait(false);

        Guid sessionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                     .CreateAsync(new CreateWorkSessionRequestModel("Research the runtime",
                                         "Answer the work item's request.",
                                         AgentWorkSessionKind.Workflow,
                                         agentId))
                                     .ConfigureAwait(false);
            sessionId = created.Id;
            AssertEx.Equal(AgentWorkSessionKind.Workflow, created.Kind);
            AssertEx.Equal(AgentWorkSessionStatus.Draft, created.Status);
        }

        // Forces the singleton factory to run: nothing has sent a turn yet, so the field is still null.
        _ = factory.Services.GetRequiredService<INodeChatStreamService>();
        var fake = AssertEx.NotNull(stream, "the fake stream service must be resolved before the loop takes a step.");
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted], (services, _) => DeclareCompleteAsync(services, sessionId)));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var started = await scope.ServiceProvider.GetRequiredService<IWorkflowOwnedWorkSessionLifecycle>().StartAsync(sessionId).ConfigureAwait(false);
            AssertEx.Equal(AgentWorkSessionStatus.Running, started.Status);
        }

        var settled = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Completed).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, settled.StepCount);
    }

    /// <summary>
    ///     The other half of the gate, across all five verbs. Enforced in the service rather than the UI, so a headless
    ///     caller cannot pause a session the run believes it is driving.
    /// </summary>
    [Test]
    [Arguments("start")]
    [Arguments("pause")]
    [Arguments("resume")]
    [Arguments("cancel")]
    [Arguments("delete")]
    public async Task WorkflowSession_RefusesEveryLifecycleVerbFromTheOrdinaryServiceSurface(string verb)
    {
        await using var factory = WorkSessionServiceTests.NewFactory();
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId, AgentWorkSessionKind.Workflow).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IWorkSessionService>();
        var refusal = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() => verb switch
                                    {
                                        "start" => service.StartAsync(sessionId),
                                        "pause" => service.PauseAsync(sessionId),
                                        "resume" => service.ResumeAsync(sessionId),
                                        "cancel" => service.CancelAsync(sessionId),
                                        _ => service.DeleteAsync(sessionId)
                                    })
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "development workflow run");

        // Refused, not half-applied: the row is untouched and still there for the run to drive.
        AssertEx.Equal(AgentWorkSessionStatus.Draft, (await WorkSessionTestSupport.ReadSessionAsync(factory.Services, sessionId).ConfigureAwait(false)).Status);
    }

    /// <summary>The mirror case: the owner surface must not reach a session no run is driving.</summary>
    [Test]
    public async Task OwnerSurface_OnASessionNoRunOwns_IsRefused()
    {
        await using var factory = WorkSessionServiceTests.NewFactory();
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var refusal = await AssertEx.ThrowsAsync<WorkSessionInvalidTransitionException>(() =>
                                        scope.ServiceProvider.GetRequiredService<IWorkflowOwnedWorkSessionLifecycle>().StartAsync(sessionId))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "belongs to no development workflow run");
    }

    /// <summary>What the <c>complete_work_session</c> tool writes: the loop reads the request and terminalizes itself.</summary>
    private static async Task DeclareCompleteAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .AppendEventAsync(new AppendWorkSessionEventCommand(sessionId,
                           WorkSessionVersions.Any,
                           WorkSessionEventTypes.CompletionRequested,
                           Guid.NewGuid(),
                           Outcome: null,
                           JsonSerializer.Serialize(new
                           {
                               summary = "The node's objective is answered."
                           })))
                       .ConfigureAwait(false);
    }
}
