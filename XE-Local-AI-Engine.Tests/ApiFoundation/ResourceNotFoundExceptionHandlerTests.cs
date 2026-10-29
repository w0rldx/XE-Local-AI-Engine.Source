namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ResourceNotFoundExceptionHandlerTests
{
    /// <summary>Every domain not-found type that used to have its own handler, plus the NodeChat ones moved onto the base.</summary>
    public static IEnumerable<Func<ResourceNotFoundException>> DomainNotFoundExceptions()
    {
        yield return () => new WorkSessionNotFoundException("gone");
        yield return () => new DevelopmentNotFoundException("gone");
        yield return () => new DevWorkflowNotFoundException("gone");
        yield return () => new GraphWorkflowNotFoundException("gone");
        yield return () => new ExternalAppNotFoundException("gone");
        yield return () => new NodeChatConversationNotFoundException(Guid.NewGuid());
        yield return () => new NodeChatMessageNotFoundException(Guid.NewGuid());
        yield return () => new NodeChatMessageCorrelationNotFoundException("gone");
    }

    [Test]
    [MethodDataSource(nameof(DomainNotFoundExceptions))]
    public async Task TryHandleAsync_ForADomainNotFound_ReturnsBodylessNotFound(ResourceNotFoundException exception)
    {
        var handler = new ResourceNotFoundExceptionHandler();
        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        AssertEx.True(handled);
        AssertEx.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
        AssertEx.Null(httpContext.Response.ContentType);
        AssertEx.Null(httpContext.Response.ContentLength);
        AssertEx.Equal(expected: 0L, responseBody.Length);
    }

    /// <summary>
    ///     The handler answers the TYPED family only. A bare <see cref="KeyNotFoundException" /> — a dictionary miss
    ///     anywhere else in the pipeline — and any other exception must still reach the 500 that says something is broken.
    /// </summary>
    [Test]
    [Arguments("key")]
    [Arguments("invalid-operation")]
    public async Task TryHandleAsync_ForAnUnrelatedException_FallsThroughWithoutChangingTheResponse(string kind)
    {
        Exception exception = kind == "key"
            ? new KeyNotFoundException("an unrelated dictionary miss")
            : new InvalidOperationException("an unrelated fault");
        var handler = new ResourceNotFoundExceptionHandler();
        var httpContext = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        AssertEx.False(handled);
        AssertEx.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        AssertEx.Null(httpContext.Response.ContentType);
    }
}
