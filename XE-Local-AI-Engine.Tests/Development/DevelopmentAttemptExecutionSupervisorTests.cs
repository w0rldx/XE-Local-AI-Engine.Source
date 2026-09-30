namespace XE_Local_AI_Engine.Tests.Development;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class DevelopmentAttemptExecutionSupervisorTests
{
    [Test]
    public async Task DisposeAsync_WhenCalledRepeatedly_RemainsIdempotent()
    {
        var supervisor = new DevelopmentAttemptExecutionSupervisor(Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IDevelopmentAttemptLiveBroker>(),
            Substitute.For<IDevelopmentAttemptLiveEventPublisher>(),
            NullLogger<DevelopmentAttemptExecutionSupervisor>.Instance);

        await supervisor.DisposeAsync();
        var repeated = supervisor.DisposeAsync();

        // Without the guard the repeat would cancel an already-disposed source and fault.
        AssertEx.True(repeated.IsCompletedSuccessfully, "a repeated dispose is a synchronous no-op.");
        await repeated;
    }
}
