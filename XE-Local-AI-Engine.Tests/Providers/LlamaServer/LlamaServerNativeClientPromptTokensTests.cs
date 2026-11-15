namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Text;
using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary><c>/apply-template</c> and <c>/tokenize</c> with pieces: root routes, the body fields each flag maps to, and how pieces parse.</summary>
[Category(TestCategories.Unit)]
public sealed class LlamaServerNativeClientPromptTokensTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:18123/v1");

    [Test]
    public async Task ApplyTemplate_PostsTheTurnToTheRoot_AndReturnsTheRenderedPrompt()
    {
        using var handler = new RecordingHandler("""{"prompt":"<|im_start|>user\nhi<|im_end|>\n"}""");
        using var http = new HttpClient(handler);

        var rendered = await Create(http).ApplyTemplateAsync(BaseAddress, "be brief", "hi", disableThinking: false, CancellationToken.None);

        AssertEx.Equal("<|im_start|>user\nhi<|im_end|>\n", rendered);
        AssertEx.Equal("http://127.0.0.1:18123/apply-template", handler.RequestUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.Body!);
        var messages = body.RootElement.GetProperty("messages");
        AssertEx.Equal(2, messages.GetArrayLength());
        AssertEx.Equal("system", messages[0].GetProperty("role").GetString());
        AssertEx.Equal("be brief", messages[0].GetProperty("content").GetString());
        AssertEx.Equal("user", messages[1].GetProperty("role").GetString());
        AssertEx.Equal("hi", messages[1].GetProperty("content").GetString());
        AssertEx.False(body.RootElement.TryGetProperty("chat_template_kwargs", out _), "Thinking on sends no template kwargs, as a chat request does.");
    }

    [Test]
    public async Task ApplyTemplate_ThinkingOff_SendsEnableThinkingFalse_AndNoSystemMessageWhenNoneIsGiven()
    {
        using var handler = new RecordingHandler("""{"prompt":"x"}""");
        using var http = new HttpClient(handler);

        await Create(http).ApplyTemplateAsync(BaseAddress, systemPrompt: null, "hi", disableThinking: true, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        AssertEx.False(body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        AssertEx.Equal(1, body.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Test]
    public async Task ApplyTemplate_ABodyWithoutPrompt_IsInvalidData()
    {
        using var handler = new RecordingHandler("""{"error":"nope"}""");
        using var http = new HttpClient(handler);

        await AssertEx.ThrowsAsync<InvalidDataException>(() => Create(http).ApplyTemplateAsync(BaseAddress, null, "hi", false, CancellationToken.None));
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(false, false)]
    public async Task TokenizeWithPieces_SendsTheCallersFlagsWithPieces_ToTheRoot(bool addSpecial, bool parseSpecial)
    {
        using var handler = new RecordingHandler("""{"tokens":[]}""");
        using var http = new HttpClient(handler);

        await Create(http).TokenizeWithPiecesAsync(BaseAddress, "hello", addSpecial, parseSpecial, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:18123/tokenize", handler.RequestUri!.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.Body!);
        AssertEx.Equal("hello", body.RootElement.GetProperty("content").GetString());
        AssertEx.Equal(addSpecial, body.RootElement.GetProperty("add_special").GetBoolean());
        AssertEx.Equal(parseSpecial, body.RootElement.GetProperty("parse_special").GetBoolean());
        AssertEx.True(body.RootElement.GetProperty("with_pieces").GetBoolean());
    }

    [Test]
    public async Task TokenizeWithPieces_ReadsTextPieces_AndSpellsBytePiecesOut()
    {
        using var handler = new RecordingHandler("""{"tokens":[{"id":151644,"piece":"<|im_start|>"},{"id":7,"piece":" hi"},{"id":228,"piece":[228,189]}]}""");
        using var http = new HttpClient(handler);

        var tokens = await Create(http).TokenizeWithPiecesAsync(BaseAddress, "x", true, true, CancellationToken.None);

        AssertEx.Equal(3, tokens.Count);
        AssertEx.Equal(151644, tokens[0].Id);
        AssertEx.Equal("<|im_start|>", tokens[0].Piece);
        AssertEx.False(tokens[0].IsBytes);
        AssertEx.Equal(" hi", tokens[1].Piece);
        AssertEx.Equal("<0xE4><0xBD>", tokens[2].Piece);
        AssertEx.True(tokens[2].IsBytes);
    }

    [Test]
    [Arguments("""{"count":3}""")]
    [Arguments("""{"tokens":[{"id":1}]}""")]
    [Arguments("""{"tokens":[{"id":1,"piece":true}]}""")]
    public async Task TokenizeWithPieces_AMalformedBody_IsInvalidData(string json)
    {
        using var handler = new RecordingHandler(json);
        using var http = new HttpClient(handler);

        await AssertEx.ThrowsAsync<InvalidDataException>(() => Create(http).TokenizeWithPiecesAsync(BaseAddress, "x", true, true, CancellationToken.None));
    }

    [Test]
    public async Task TokenizeWithPieces_ANonSuccessStatus_Throws()
    {
        using var handler = new RecordingHandler("""{"error":{"message":"busy"}}""", HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);

        var exception = await AssertEx.ThrowsAsync<HttpRequestException>(() => Create(http).TokenizeWithPiecesAsync(BaseAddress, "x", true, true, CancellationToken.None));

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    private static LlamaServerNativeClient Create(HttpClient tokenizeClient) =>
        new(Substitute.For<IHttpClientFactory>(), tokenizeClient);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;

        public RecordingHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _json = json;
            _status = status;
        }

        public Uri? RequestUri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };
        }
    }
}
