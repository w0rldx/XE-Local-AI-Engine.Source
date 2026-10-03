namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     "Answer now" may only reach llama-server for a running turn that is reasoning on an armed completion, and the
///     request must be the exact control call the pinned build documents.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InvocationReasoningControlTests
{
    private static readonly Uri ServerBase = new("http://127.0.0.1:5810/v1");

    [Test]
    public async Task EndReasoning_ForAnUnknownMessage_IsNotFound()
    {
        var (control, native) = Create();

        AssertEx.Equal(ReasoningEndOutcome.NotFound, await control.EndReasoningAsync(Guid.NewGuid(), CancellationToken.None));
        await native.DidNotReceiveWithAnyArgs().EndReasoningAsync(default!, default!, default);
    }

    [Test]
    public async Task EndReasoning_BeforeTheCompletionIsArmed_IsNotReasoning()
    {
        var (control, native) = Create();
        var messageId = Guid.NewGuid();
        using var turn = control.Track(messageId);

        // Reasoning text from a stream that never advertised a completion: a cloud or Ollama turn, or thinking off.
        turn.Observe(new ChatResponseUpdate(ChatRole.Assistant, "x"), sawReasoning: true, sawAnswer: false);

        AssertEx.Equal(ReasoningEndOutcome.NotReasoning, await control.EndReasoningAsync(messageId, CancellationToken.None));
        await native.DidNotReceiveWithAnyArgs().EndReasoningAsync(default!, default!, default);
    }

    [Test]
    public async Task EndReasoning_WhileTheArmedCompletionReasons_EndsItOnTheAdvertisedServer()
    {
        var (control, native) = Create();
        native.EndReasoningAsync(ServerBase, "chatcmpl-1", Arg.Any<CancellationToken>()).Returns(new LlamaServerReasoningControlResult { Success = true, Message = "ok" });
        var messageId = Guid.NewGuid();
        using var turn = control.Track(messageId);

        turn.Observe(Advertised("chatcmpl-1"), sawReasoning: true, sawAnswer: false);

        AssertEx.Equal(ReasoningEndOutcome.Ended, await control.EndReasoningAsync(messageId, CancellationToken.None));
        await native.Received(1).EndReasoningAsync(ServerBase, "chatcmpl-1", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EndReasoning_OnceTheAnswerStarted_IsNotReasoning()
    {
        var (control, _) = Create();
        var messageId = Guid.NewGuid();
        using var turn = control.Track(messageId);

        turn.Observe(Advertised("chatcmpl-2"), sawReasoning: true, sawAnswer: false);
        turn.Observe(null, sawReasoning: false, sawAnswer: true);

        AssertEx.Equal(ReasoningEndOutcome.NotReasoning, await control.EndReasoningAsync(messageId, CancellationToken.None));
    }

    [Test]
    public async Task EndReasoning_OnTheNextToolRoundsCompletion_AddressesTheNewId()
    {
        var (control, native) = Create();
        native.EndReasoningAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new LlamaServerReasoningControlResult { Success = true, Message = null });
        var messageId = Guid.NewGuid();
        using var turn = control.Track(messageId);

        turn.Observe(Advertised("chatcmpl-round-1"), sawReasoning: true, sawAnswer: false);
        turn.Observe(null, sawReasoning: false, sawAnswer: true);
        turn.Observe(Advertised("chatcmpl-round-2"), sawReasoning: true, sawAnswer: false);

        AssertEx.Equal(ReasoningEndOutcome.Ended, await control.EndReasoningAsync(messageId, CancellationToken.None));
        await native.Received(1).EndReasoningAsync(ServerBase, "chatcmpl-round-2", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EndReasoning_WhenTheServerRefusesOrIsGone_IsRejected()
    {
        var (control, native) = Create();
        var refused = Guid.NewGuid();
        var gone = Guid.NewGuid();
        native.EndReasoningAsync(ServerBase, "chatcmpl-refused", Arg.Any<CancellationToken>()).Returns(new LlamaServerReasoningControlResult { Success = false, Message = "not armed" });
        native.EndReasoningAsync(ServerBase, "chatcmpl-gone", Arg.Any<CancellationToken>())
              .Returns<Task<LlamaServerReasoningControlResult>>(_ => throw new HttpRequestException("connection refused"));
        using var refusedTurn = control.Track(refused);
        using var goneTurn = control.Track(gone);
        refusedTurn.Observe(Advertised("chatcmpl-refused"), sawReasoning: true, sawAnswer: false);
        goneTurn.Observe(Advertised("chatcmpl-gone"), sawReasoning: true, sawAnswer: false);

        AssertEx.Equal(ReasoningEndOutcome.Rejected, await control.EndReasoningAsync(refused, CancellationToken.None));
        AssertEx.Equal(ReasoningEndOutcome.Rejected, await control.EndReasoningAsync(gone, CancellationToken.None));
    }

    [Test]
    public async Task EndReasoning_AfterTheTurnEnded_IsNotFound()
    {
        var (control, _) = Create();
        var messageId = Guid.NewGuid();
        var turn = control.Track(messageId);
        turn.Observe(Advertised("chatcmpl-3"), sawReasoning: true, sawAnswer: false);

        turn.Dispose();

        AssertEx.Equal(ReasoningEndOutcome.NotFound, await control.EndReasoningAsync(messageId, CancellationToken.None));
    }

    [Test]
    [Arguments(HttpStatusCode.OK, "{\"success\":true,\"message\":\"reasoning ended\"}", true, "reasoning ended")]
    [Arguments(HttpStatusCode.OK, "{\"success\":false,\"message\":\"already finished\"}", false, "already finished")]
    [Arguments(HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"message\":\"control not armed\"}}", false, "control not armed")]
    [Arguments(HttpStatusCode.NotFound, "File Not Found", false, "File Not Found")]
    public async Task NativeClient_PostsTheDocumentedControlCallAndReadsTheVerdict(HttpStatusCode status, string responseBody, bool success, string message)
    {
        using var handler = new RecordingHandler(status, responseBody);
        using var http = new HttpClient(handler);
        var client = new LlamaServerNativeClient(Substitute.For<IHttpClientFactory>(), http);

        var result = await client.EndReasoningAsync(ServerBase, "chatcmpl-9", CancellationToken.None);

        AssertEx.Equal(success, result.Success);
        AssertEx.Equal(message, result.Message);
        AssertEx.Equal(new Uri("http://127.0.0.1:5810/v1/chat/completions/control"), handler.RequestUri);
        using var sent = JsonDocument.Parse(AssertEx.NotNull(handler.Body));
        AssertEx.Equal("chatcmpl-9", sent.RootElement.GetProperty("id").GetString());
        AssertEx.Equal("reasoning_end", sent.RootElement.GetProperty("action").GetString());
    }

    private static (InvocationReasoningControl Control, ILlamaServerNativeClient Native) Create()
    {
        var native = Substitute.For<ILlamaServerNativeClient>();
        return (new InvocationReasoningControl(native, NullLogger<InvocationReasoningControl>.Instance), native);
    }

    private static ChatResponseUpdate Advertised(string completionId) => new(ChatRole.Assistant, string.Empty)
    {
        ResponseId = completionId,
        AdditionalProperties = new AdditionalPropertiesDictionary { [LlamaServerReasoningControl.EndpointKey] = ServerBase }
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;

        public RecordingHandler(HttpStatusCode status, string responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        public Uri? RequestUri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent(_responseBody, Encoding.UTF8, "application/json") };
        }
    }
}
