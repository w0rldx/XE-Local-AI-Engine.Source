namespace XE_Local_AI_Engine.Tests.Containers.Bridge;

using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The per-instance token requirement on every bridge route. The peer guard admits any container on an
///     engine-owned network, so this is the layer that stops one of them using another's bridge — which is why an
///     unverified request must never reach whatever is mapped behind it.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerBridgeTokenMiddlewareTests
{
    private const string ValidToken = "0123456789abcdef0123456789abcdef.c2VjcmV0LW1hdGVyaWFs";

    [Test]
    [Arguments(null, "no Authorization header at all")]
    [Arguments("", "an empty Authorization header")]
    [Arguments("Basic dXNlcjpwYXNz", "a non-bearer scheme")]
    [Arguments("Bearer", "a bearer scheme with nothing after it")]
    [Arguments("Bearer    ", "a bearer scheme with only whitespace after it")]
    public async Task Bridge_WhenNoUsableBearerTokenIsPresented_Answers401AndNeverAsksTheVerifier(string? header, string description)
    {
        var verifier = Substitute.For<IContainerBridgeTokenVerifier>();
        var context = CreateContext(header);
        var nextCalled = false;

        await CreateMiddleware(verifier).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        AssertEx.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        AssertEx.False(nextCalled, $"A request with {description} must never reach anything behind the token gate.");
        AssertEx.Equal("Bearer", context.Response.Headers[HeaderNames.WWWAuthenticate].ToString());
        await verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!).ConfigureAwait(false);
    }

    [Test]
    public async Task Bridge_WhenTheTokenDoesNotVerify_Answers401WithTheOpenAiErrorEnvelope()
    {
        var verifier = Substitute.For<IContainerBridgeTokenVerifier>();
        _ = verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ContainerBridgeCaller?)null);
        var context = CreateContext("Bearer " + ValidToken);
        var body = new MemoryStream();
        context.Response.Body = body;
        var nextCalled = false;

        await CreateMiddleware(verifier).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        AssertEx.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        AssertEx.False(nextCalled, "A token that did not verify must never reach anything behind the gate.");
        AssertEx.Equal(ContainerBridgeTokenMiddleware.UnauthorizedBody, Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>
    ///     The refusal body must not say WHICH half failed. A caller that can tell "no such instance" from "wrong
    ///     secret" can enumerate the instance ids this node has installed.
    /// </summary>
    [Test]
    public async Task Bridge_AnswersEveryRefusalIdentically()
    {
        var verifier = Substitute.For<IContainerBridgeTokenVerifier>();
        _ = verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ContainerBridgeCaller?)null);

        var missing = CreateContext(header: null);
        missing.Response.Body = new MemoryStream();
        var wrong = CreateContext("Bearer " + ValidToken);
        wrong.Response.Body = new MemoryStream();

        var middleware = CreateMiddleware(verifier);
        await middleware.InvokeAsync(missing, static _ => Task.CompletedTask).ConfigureAwait(false);
        await middleware.InvokeAsync(wrong, static _ => Task.CompletedTask).ConfigureAwait(false);

        AssertEx.Equal(missing.Response.StatusCode, wrong.Response.StatusCode);
        AssertEx.Equal(Encoding.UTF8.GetString(((MemoryStream)missing.Response.Body).ToArray()),
            Encoding.UTF8.GetString(((MemoryStream)wrong.Response.Body).ToArray()));
    }

    [Test]
    public async Task Bridge_WhenTheTokenVerifies_StashesTheCallerAndContinues()
    {
        var instanceId = Guid.NewGuid();
        var verifier = Substitute.For<IContainerBridgeTokenVerifier>();
        _ = verifier.VerifyAsync(ValidToken, Arg.Any<CancellationToken>()).Returns(new ContainerBridgeCaller(instanceId));
        var context = CreateContext("Bearer " + ValidToken);
        var nextCalled = false;

        await CreateMiddleware(verifier).InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        AssertEx.True(nextCalled, "A verified token must reach the routes behind the gate.");
        AssertEx.Equal(instanceId, AssertEx.NotNull(context.Features.Get<ContainerBridgeCaller>()).InstanceId,
            "The routes behind this read the caller by type off the request's features.");
    }

    /// <summary>The scheme word is case-insensitive per RFC 7235; the token after it is not touched.</summary>
    [Test]
    public async Task Bridge_AcceptsTheBearerSchemeInAnyCase()
    {
        var verifier = Substitute.For<IContainerBridgeTokenVerifier>();
        _ = verifier.VerifyAsync(ValidToken, Arg.Any<CancellationToken>()).Returns(new ContainerBridgeCaller(Guid.NewGuid()));
        var context = CreateContext("bEaReR " + ValidToken);

        await CreateMiddleware(verifier).InvokeAsync(context, static _ => Task.CompletedTask).ConfigureAwait(false);

        AssertEx.NotNull(context.Features.Get<ContainerBridgeCaller>());
    }

    private static ContainerBridgeTokenMiddleware CreateMiddleware(IContainerBridgeTokenVerifier verifier)
    {
        return new ContainerBridgeTokenMiddleware(verifier, NullLogger<ContainerBridgeTokenMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(string? header)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/llm/v1/chat/completions";
        if (header is not null)
        {
            context.Request.Headers.Authorization = header;
        }

        return context;
    }
}
