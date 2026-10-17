namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The shutdown guard's decision: refuse with 503, close and retry-after once stopping, pass through before.</summary>
[Category(TestCategories.Unit)]
public sealed class ShutdownRequestGuardTests
{
    [Test]
    public async Task TryRefuse_OnceStopping_Answers503WithCloseAndRetryAfter()
    {
        var context = new DefaultHttpContext();

        var refused = ShutdownRequestGuard.TryRefuse(context, new CancellationToken(canceled: true));

        AssertEx.True(refused);
        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Equal("close", context.Response.Headers[HeaderNames.Connection].ToString());
        AssertEx.Equal(ShutdownRequestGuard.RetryAfterSeconds, context.Response.Headers[HeaderNames.RetryAfter].ToString());
    }

    [Test]
    public async Task TryRefuse_WhileRunning_LeavesTheResponseUntouched()
    {
        var context = new DefaultHttpContext();

        var refused = ShutdownRequestGuard.TryRefuse(context, CancellationToken.None);

        AssertEx.False(refused);
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertEx.Empty(context.Response.Headers);
    }
}
