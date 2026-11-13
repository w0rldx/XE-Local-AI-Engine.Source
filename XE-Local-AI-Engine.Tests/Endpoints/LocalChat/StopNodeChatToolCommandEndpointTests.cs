namespace XE_Local_AI_Engine.Tests.Endpoints.LocalChat;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The tool-command stop route over the real pipeline: 404 with nothing running for that call, 202 once the registered
///     command was asked to stop (it then ends as not completed, the result the tool renders for the model), Operator only.
/// </summary>
/// <remarks>The stop registry is the host's own singleton; the command runs on the deterministic fake sandbox.</remarks>
[Category(TestCategories.Integration)]
public sealed class StopNodeChatToolCommandEndpointTests
{
    private const string CallId = "call-7";
    private const string Command = "dotnet test";

    [Test]
    public async Task Stop_ForACallRunningNoCommand_IsNotFound()
    {
        await using var factory = new TestServerWebAppFactory();

        using var response = await SendAsync(factory, Guid.NewGuid(), CallId, authenticated: true);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task Stop_ForARunningCommand_EndsItAsNotCompleted_AndTouchesNoOtherCall()
    {
        await using var factory = new TestServerWebAppFactory();
        var stops = factory.Services.GetRequiredService<ToolCommandStopRegistry>();
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        provider.RegisterBlockingCommand(Command);
        var handle = await CreateHandleAsync(provider);
        var messageId = Guid.NewGuid();

        var running = provider.ExecuteAsync(handle, new SandboxCommandRequest
        {
            ExecutionId = "exec-1",
            Executable = "dotnet",
            Arguments = ["test"]
        });
        using var registration = stops.Register(messageId, CallId, provider, handle, "exec-1");

        using (var otherCall = await SendAsync(factory, messageId, "call-8", authenticated: true))
        {
            AssertEx.Equal(HttpStatusCode.NotFound, otherCall.StatusCode, "a stop reaches only the command registered for that call");
        }

        using (var otherMessage = await SendAsync(factory, Guid.NewGuid(), CallId, authenticated: true))
        {
            AssertEx.Equal(HttpStatusCode.NotFound, otherMessage.StatusCode, "the same call id on another message's turn is a different call");
        }

        AssertEx.False(running.IsCompleted);

        using var response = await SendAsync(factory, messageId, CallId, authenticated: true);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(30));
        AssertEx.False(result.Completed, "a stopped command ends as not completed, without throwing into the tool");
    }

    [Test]
    public async Task Stop_WithoutTheNodeToken_IsUnauthorized()
    {
        await using var factory = new TestServerWebAppFactory();

        using var response = await SendAsync(factory, Guid.NewGuid(), CallId, authenticated: false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<SandboxHandle> CreateHandleAsync(FakeSandboxRuntimeProvider provider) =>
        provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = provider.ProviderName,
                RuntimeProfile = "stop-test",
                ManifestVersion = 1
            },
            RuntimeProfile = "stop-test"
        });

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, Guid messageId, string toolCallId, bool authenticated)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/local/v1/chat/messages/{messageId}/tool-calls/{toolCallId}/stop");
        if (authenticated)
        {
            factory.AddNodeBearerToken(request);
        }

        return await client.SendAsync(request);
    }
}
