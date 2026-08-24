namespace XE_Local_AI_Engine.Tests.WorkSessions;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Repointing a session at a cloud model.
///     <para>
///         The knowledge-base cloud gate is per turn and acts on the OFFER: it withholds the local-data tools from a
///         cloud model. It says nothing about text a local model has already extracted. Without the check under test,
///         researching on a local model, pausing, repointing at a cloud agent and resuming would hand the whole findings
///         corpus to a third-party provider inside the next step's state block.
///     </para>
/// </summary>
public sealed class WorkSessionCloudEgressTests
{
    [Test]
    public async Task Update_RepointingASessionWithFindingsAtACloudAgent_IsRefused()
    {
        await using var factory = WorkSessionServiceTests.NewFactory();
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await RecordAFindingAsync(factory, sessionId).ConfigureAwait(false);
        var cloudAgentId = await WorkSessionServiceTests.SeedAgentAsync(factory, "a-cloud-model").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var refusal = await AssertEx.ThrowsAsync<WorkSessionValidationException>(() =>
                                        scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                             .UpdateAsync(sessionId, new UpdateWorkSessionRequestModel(null, null, cloudAgentId)))
                                    .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "send them off the node");
    }

    [Test]
    public async Task Update_RepointingASessionWithNoFindingsAtACloudAgent_IsAllowed()
    {
        await using var factory = WorkSessionServiceTests.NewFactory();
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var cloudAgentId = await WorkSessionServiceTests.SeedAgentAsync(factory, "a-cloud-model").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                 .UpdateAsync(sessionId, new UpdateWorkSessionRequestModel(null, null, cloudAgentId))
                                 .ConfigureAwait(false);

        AssertEx.Equal(cloudAgentId, updated.AgentDefinitionId, "There is nothing extracted yet, so there is nothing to keep on the node.");
    }

    [Test]
    public async Task Update_WhenTheOperatorOptedCloudDataAccessIn_AllowsTheRepoint()
    {
        await using var factory = WorkSessionServiceTests.NewFactory(("KnowledgeBase:AllowCloudModelAccess", "true"));
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await RecordAFindingAsync(factory, sessionId).ConfigureAwait(false);
        var cloudAgentId = await WorkSessionServiceTests.SeedAgentAsync(factory, "a-cloud-model").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                 .UpdateAsync(sessionId, new UpdateWorkSessionRequestModel(null, null, cloudAgentId))
                                 .ConfigureAwait(false);

        AssertEx.Equal(cloudAgentId, updated.AgentDefinitionId, "The operator's opt-in is the one thing that makes this egress intentional.");
    }

    [Test]
    public async Task Update_RepointingASessionWithFindingsAtAnotherLocalAgent_IsAllowed()
    {
        await using var factory = WorkSessionServiceTests.NewFactory();
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await RecordAFindingAsync(factory, sessionId).ConfigureAwait(false);
        var localAgentId = await WorkSessionServiceTests.SeedAgentAsync(factory, "another-local-model").ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<IWorkSessionService>()
                                 .UpdateAsync(sessionId, new UpdateWorkSessionRequestModel(null, null, localAgentId))
                                 .ConfigureAwait(false);

        AssertEx.Equal(localAgentId, updated.AgentDefinitionId, "The gate is about leaving the node, not about changing agents.");
    }

    private static async Task RecordAFindingAsync(TestServerWebAppFactory factory, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>()
                       .AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                           Guid.NewGuid(),
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionFindingKind.Finding,
                           "The inference path runs on llama.cpp by default."))
                       .ConfigureAwait(false);
    }
}
