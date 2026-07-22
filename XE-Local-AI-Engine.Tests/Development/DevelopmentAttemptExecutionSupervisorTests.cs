namespace XE_Local_AI_Engine.Tests.Development;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class DevelopmentAttemptExecutionSupervisorTests
{
    [Test]
    public async Task DisposeAsync_WhenCalledRepeatedly_RemainsIdempotent()
    {
        var supervisor = new DevelopmentAttemptExecutionSupervisor(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IDevelopmentAttemptLiveBroker>(),
            Substitute.For<IDevelopmentAttemptLiveEventPublisher>(),
            NullLogger<DevelopmentAttemptExecutionSupervisor>.Instance);

        await supervisor.DisposeAsync();
        await supervisor.DisposeAsync();
    }
}
