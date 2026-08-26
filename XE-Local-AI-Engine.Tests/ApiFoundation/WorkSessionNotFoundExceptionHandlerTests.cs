namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using Microsoft.AspNetCore.Http;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WorkSessionNotFoundExceptionHandlerTests
{
    [Test]
    public async Task TryHandleAsync_WhenWorkSessionResourceIsMissing_ReturnsBodylessNotFound()
    {
        var handler = new WorkSessionNotFoundExceptionHandler();
        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;

        var handled = await handler.TryHandleAsync(httpContext, new WorkSessionNotFoundException("gone"), CancellationToken.None)
                                   .ConfigureAwait(false);

        AssertEx.True(handled);
        AssertEx.Equal(StatusCodes.Status404NotFound, httpContext.Response.StatusCode);
        AssertEx.Null(httpContext.Response.ContentType);
        AssertEx.Equal(expected: 0L, responseBody.Length);
    }

    [Test]
    public async Task TryHandleAsync_WhenKeyNotFoundIsUnrelated_FallsThroughWithoutChangingTheResponse()
    {
        var handler = new WorkSessionNotFoundExceptionHandler();
        var httpContext = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(httpContext, new KeyNotFoundException("unrelated"), CancellationToken.None)
                                   .ConfigureAwait(false);

        AssertEx.False(handled);
        AssertEx.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        AssertEx.Null(httpContext.Response.ContentType);
    }
}
