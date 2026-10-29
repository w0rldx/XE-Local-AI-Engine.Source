namespace XE_Local_AI_Engine.Tests.Tutorial;

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Tutorial;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The tutorial writes' outcomes against the real service and the real Identity pipeline: a principal with no node
///     user, a write Identity refuses, and the write that lands.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeTutorialStateServiceTests
{
    private const string UserId = "tutorial-service-user";

    [Test]
    public async Task SaveEntryAsync_ReportsSaved_UserNotResolved_AndNotPersisted()
    {
        var refusing = new RefusingNodeUserValidator();
        await using var factory = NewFactory(refusing);
        await SeedUserAsync(factory);

        AssertEx.Equal(TutorialEntrySaveOutcome.Saved, await SaveAsync(factory, Principal(factory, UserId)));
        AssertEx.Equal(TutorialEntrySaveOutcome.UserNotResolved, await SaveAsync(factory, Principal(factory, "no-such-user")));

        refusing.Refuse = true;
        AssertEx.Equal(TutorialEntrySaveOutcome.NotPersisted, await SaveAsync(factory, Principal(factory, UserId)));
    }

    [Test]
    public async Task AcknowledgeAiAsync_ReportsAcknowledged_UserNotResolved_AndNotPersisted()
    {
        var refusing = new RefusingNodeUserValidator();
        await using var factory = NewFactory(refusing);
        await SeedUserAsync(factory);

        refusing.Refuse = true;
        AssertEx.True(await AcknowledgeAsync(factory, Principal(factory, UserId)) is AiAcknowledgementResult.NotPersisted,
            "a refused first acknowledgement stores nothing and says so.");
        AssertEx.True(await AcknowledgeAsync(factory, Principal(factory, "no-such-user")) is AiAcknowledgementResult.UserNotResolved);

        refusing.Refuse = false;
        var acknowledged = AssertEx.NotNull(await AcknowledgeAsync(factory, Principal(factory, UserId)) as AiAcknowledgementResult.Acknowledged);
        AssertEx.Equal(DateTimeKind.Utc, acknowledged.AtUtc.Kind);
    }

    private static TestServerWebAppFactory NewFactory(RefusingNodeUserValidator refusing) =>
        new()
        {
            ConfigureAdditionalTestServices = services => services.AddSingleton<IUserValidator<NodeUser>>(refusing)
        };

    private static async Task SeedUserAsync(TestServerWebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>().CreateAsync(new NodeUser
        {
            Id = UserId,
            UserName = "tutorial@example.test",
            Email = "tutorial@example.test",
            SetupCompleted = true
        });
        AssertEx.True(result.Succeeded, "the fixture user must exist before the refusal is switched on.");
    }

    private static ClaimsPrincipal Principal(TestServerWebAppFactory factory, string userId)
    {
        var claimType = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.UserIdClaimType;
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, userId)], authenticationType: "test"));
    }

    private static async Task<TutorialEntrySaveOutcome> SaveAsync(TestServerWebAppFactory factory, ClaimsPrincipal principal)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INodeTutorialStateService>()
                          .SaveEntryAsync(principal, "main-app-v1", TutorialStatus.Completed, CancellationToken.None);
    }

    private static async Task<AiAcknowledgementResult> AcknowledgeAsync(TestServerWebAppFactory factory, ClaimsPrincipal principal)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INodeTutorialStateService>().AcknowledgeAiAsync(principal, CancellationToken.None);
    }
}
