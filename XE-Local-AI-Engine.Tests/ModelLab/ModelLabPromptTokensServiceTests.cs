namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Over the real native client and a scripted llama-server: never loads a model, renders the run's messages, marks control tokens, caps the list.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelLabPromptTokensServiceTests
{
    private const string Model = "org/Some-GGUF:Q4_K_M";

    [Test]
    public async Task AModelThatIsNotInstalled_IsNull_WithoutLookingForItsProcess()
    {
        var harness = new Harness();
        harness.Models.ExistsAsync(Model, Arg.Any<CancellationToken>()).Returns(false);

        var tokens = await harness.Service.GetPromptTokensAsync(Query("hi"), CancellationToken.None);

        AssertEx.Null(tokens);
        harness.Supervisor.DidNotReceiveWithAnyArgs().FindRunningEndpoint(null!, default);
        AssertEx.Empty(harness.Server.Requests);
    }

    [Test]
    public async Task AModelNotRunning_IsNull_AndNothingEnsuresOrCallsTheServer()
    {
        var harness = new Harness();
        harness.Supervisor.FindRunningEndpoint(Model, ModelRole.Chat).Returns((LlamaServerEndpoint?)null);

        var tokens = await harness.Service.GetPromptTokensAsync(Query("hi"), CancellationToken.None);

        AssertEx.Null(tokens);
        await harness.Supervisor.DidNotReceiveWithAnyArgs().EnsureRunningAsync(null!, default, CancellationToken.None);
        await harness.Supervisor.DidNotReceiveWithAnyArgs().EnsureRunningAsync(null!, default, default, CancellationToken.None);
        AssertEx.Empty(harness.Server.Requests);
    }

    [Test]
    public async Task TheRenderedPrompt_IsTokenizedLikeAChatCompletion_AndOnlyControlTokensAreSpecial()
    {
        var harness = new Harness();

        var tokens = AssertEx.NotNull(await harness.Service.GetPromptTokensAsync(Query("hi"), CancellationToken.None));

        AssertEx.Equal(ScriptedServer.Template("hi"), tokens.RenderedPrompt);
        AssertEx.Equal("<s>|<|im_start|>|user|\n|hi|<|im_end|>|\n", string.Join('|', tokens.Tokens.Select(static token => token.Piece)));
        AssertEx.Equal("<s>,<|im_start|>,<|im_end|>", string.Join(',', tokens.Tokens.Where(static token => token.Special).Select(static token => token.Piece).Distinct()));
        AssertEx.Equal(tokens.Tokens.Count, tokens.TokenCount);
        AssertEx.False(tokens.Truncated);
        var prompt = harness.Server.Requests.First(static request => request.Path == "/tokenize");
        AssertEx.True(prompt.Body["add_special"]!.GetValue<bool>() && prompt.Body["parse_special"]!.GetValue<bool>(),
            "The rendered prompt is tokenized with BOS added and control-token text parsed, as the chat path does.");
    }

    [Test]
    public async Task EachDistinctPiece_IsCheckedOnce()
    {
        var harness = new Harness();

        await harness.Service.GetPromptTokensAsync(Query("hi hi hi"), CancellationToken.None);

        var checks = harness.Server.Requests.Where(static request => request.Path == "/tokenize").Skip(1).ToArray();
        AssertEx.Equal(checks.Length, checks.Select(static request => $"{request.Body["parse_special"]}:{request.Body["content"]}").Distinct(StringComparer.Ordinal).Count(),
            "No piece is re-tokenized twice in the same mode.");
        AssertEx.True(checks.All(static request => !request.Body["add_special"]!.GetValue<bool>()), "A piece is checked without BOS.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TheThinkingSwitch_RidesTheTemplateKwargs_OnlyWhenOff(bool disableThinking)
    {
        var harness = new Harness();

        await harness.Service.GetPromptTokensAsync(Query("hi", disableThinking: disableThinking), CancellationToken.None);

        var body = harness.Server.Requests.Single(static request => request.Path == "/apply-template").Body;
        AssertEx.Equal(disableThinking, body["chat_template_kwargs"] is JsonObject kwargs && !kwargs["enable_thinking"]!.GetValue<bool>());
    }

    [Test]
    [Arguments("   ", 1)]
    [Arguments("be brief", 2)]
    public async Task ABlankSystemPrompt_SendsNoSystemMessage(string systemPrompt, int expectedMessages)
    {
        var harness = new Harness();

        await harness.Service.GetPromptTokensAsync(Query("hi", systemPrompt), CancellationToken.None);

        var body = harness.Server.Requests.Single(static request => request.Path == "/apply-template").Body;
        AssertEx.Equal(expectedMessages, body["messages"]!.AsArray().Count);
    }

    [Test]
    public async Task ALongPrompt_ListsTheCap_AndCountsEveryToken()
    {
        var harness = new Harness();
        var prompt = string.Concat(Enumerable.Repeat("hi ", ModelLabPromptTokensService.MaxListedTokens));

        var tokens = AssertEx.NotNull(await harness.Service.GetPromptTokensAsync(Query(prompt), CancellationToken.None));

        AssertEx.Equal(ModelLabPromptTokensService.MaxListedTokens, tokens.Tokens.Count);
        AssertEx.True(tokens.TokenCount > ModelLabPromptTokensService.MaxListedTokens, "The count covers the tokens cut from the list.");
        AssertEx.True(tokens.Truncated);
    }

    [Test]
    public async Task ANonLoopbackEndpoint_IsRefusedBeforeAnyRequest()
    {
        var harness = new Harness(new Uri("http://10.0.0.5:18123/v1"));

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.Service.GetPromptTokensAsync(Query("hi"), CancellationToken.None));

        AssertEx.Empty(harness.Server.Requests);
    }

    [Test]
    public async Task AServerGoneMidCall_IsNull()
    {
        var harness = new Harness();
        harness.Server.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "refused");

        AssertEx.Null(await harness.Service.GetPromptTokensAsync(Query("hi"), CancellationToken.None));
    }

    [Test]
    public async Task AServerError_Propagates_InsteadOfAnEmptyAnswer()
    {
        var harness = new Harness();
        harness.Server.Status = HttpStatusCode.InternalServerError;

        await AssertEx.ThrowsAsync<HttpRequestException>(() => harness.Service.GetPromptTokensAsync(Query("hi"), CancellationToken.None));
    }

    private static ModelLabPromptTokensQuery Query(string prompt, string? systemPrompt = null, bool disableThinking = false) =>
        new()
        {
            ModelName = Model,
            SystemPrompt = systemPrompt,
            Prompt = prompt,
            DisableThinking = disableThinking
        };

    private sealed class Harness
    {
        public Harness(Uri? baseAddress = null)
        {
            Models.ExistsAsync(Model, Arg.Any<CancellationToken>()).Returns(true);
            var providers = Substitute.For<ILocalModelProviderResolver>();
            providers.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(LlamaServerProviderConstants.ProviderName);
            Supervisor.FindRunningEndpoint(Model, ModelRole.Chat).Returns(new LlamaServerEndpoint
            {
                ModelName = Model,
                Role = ModelRole.Chat,
                BaseAddress = baseAddress ?? new Uri("http://127.0.0.1:18123/v1")
            });
            Http = new HttpClient(Server);
            Service = new ModelLabPromptTokensService(Models,
                new FakeModelTrustResolver(),
                providers,
                Supervisor,
                new LlamaServerNativeClient(Substitute.For<IHttpClientFactory>(), Http));
        }

        public IGgufModelStore Models { get; } = Substitute.For<IGgufModelStore>();

        public ILlamaServerProcessSupervisor Supervisor { get; } = Substitute.For<ILlamaServerProcessSupervisor>();

        public ScriptedServer Server { get; } = new();

        public HttpClient Http { get; }

        public ModelLabPromptTokensService Service { get; }
    }

    /// <summary>A ChatML-like template and a longest-match tokenizer whose control tokens match only with <c>parse_special</c>, as in llama.cpp; other characters stand alone.</summary>
    private sealed class ScriptedServer : HttpMessageHandler
    {
        private static readonly Dictionary<string, int> Control = new(StringComparer.Ordinal)
        {
            ["<s>"] = 1,
            ["<|im_start|>"] = 2,
            ["<|im_end|>"] = 3
        };

        private static readonly Dictionary<string, int> Plain = new(StringComparer.Ordinal)
        {
            ["user"] = 10,
            ["hi"] = 11
        };

        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public Exception? Failure { get; set; }

        public static string Template(string user) =>
            $"<|im_start|>user\n{user}<|im_end|>\n";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            var body = AssertEx.NotNull(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))).AsObject();
            var path = request.RequestUri!.AbsolutePath;
            Requests.Enqueue(new RecordedRequest
            {
                Path = path,
                Body = body
            });
            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status);
            }

            object payload = path switch
            {
                "/apply-template" => new
                {
                    prompt = Template(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>())
                },
                "/tokenize" => new
                {
                    tokens = Tokenize(body["content"]!.GetValue<string>(), body["add_special"]!.GetValue<bool>(), body["parse_special"]!.GetValue<bool>())
                },
                _ => throw new InvalidOperationException($"Unexpected route {path}.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload, options: JsonSerializerOptions.Web)
            };
        }

        private static List<object> Tokenize(string content, bool addSpecial, bool parseSpecial)
        {
            List<object> tokens = addSpecial ? [new { id = Control["<s>"], piece = "<s>" }] : [];
            var position = 0;
            while (position < content.Length)
            {
                var match = (parseSpecial ? Control.Concat(Plain) : Plain)
                            .Where(entry => string.CompareOrdinal(content, position, entry.Key, 0, entry.Key.Length) == 0)
                            .OrderByDescending(static entry => entry.Key.Length)
                            .FirstOrDefault();
                var piece = match.Key ?? content[position].ToString();
                tokens.Add(new
                {
                    id = match.Key is null ? 1000 + content[position] : match.Value,
                    piece
                });
                position += piece.Length;
            }

            return tokens;
        }
    }

    private sealed class RecordedRequest
    {
        public required string Path { get; init; }

        public required JsonObject Body { get; init; }
    }
}
