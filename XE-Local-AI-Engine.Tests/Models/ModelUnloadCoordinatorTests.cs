namespace XE_Local_AI_Engine.Tests.Models;

using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The unload endpoint's coordinator ejects as the operator, over the real supervisor, so background work cannot reload the model in any role.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelUnloadCoordinatorTests
{
    private const string Model = "model-a";

    [Test]
    public async Task UnloadAsync_MarksEveryRole_SoNoBackgroundLoadRespawnsIt()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.True(await new ModelUnloadCoordinator(supervisor).UnloadAsync(Model));

        // Chat was running; every other role was not, and is marked all the same.
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            await AssertEx.ThrowsAsync<OperatorEjectedException>(() => supervisor.EnsureRunningAsync(Model, role, ModelResidencyIntent.Background, CancellationToken.None));
        }

        AssertEx.Equal(expected: 1, launcher.LaunchCount, "A Background load after an operator unload must never reach the launcher.");
    }
}
