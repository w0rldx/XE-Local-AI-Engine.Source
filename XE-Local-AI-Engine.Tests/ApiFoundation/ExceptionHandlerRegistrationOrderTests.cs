namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ExceptionHandlerRegistrationOrderTests
{
    [Test]
    public async Task Handlers_AreRegisteredFromSpecificContractsToTheDefaultFallback()
    {
        await using var factory = new TestServerWebAppFactory();

        var handlerTypes = factory.Services.GetServices<IExceptionHandler>().Select(static handler => handler.GetType().Name).ToArray();

        AssertEx.Equal(string.Join(Environment.NewLine,
                nameof(ConflictExceptionHandler),
                nameof(DomainValidationExceptionHandler),
                nameof(TrainingExceptionHandler),
                nameof(BenchmarkExceptionHandler),
                nameof(WorkSessionNotFoundExceptionHandler),
                nameof(DevWorkflowNotFoundExceptionHandler),
                nameof(GraphWorkflowNotFoundExceptionHandler),
                nameof(DefaultExceptionHandler)),
            string.Join(Environment.NewLine, handlerTypes),
            "Exception-handler order is behavioral: the default handler must remain last and family handlers must not preempt shared contracts.");
    }
}
