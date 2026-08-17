namespace XE_Local_AI_Engine.Tests.Training.Export;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Training.Export;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The gate's verdicts, against a scripted transient server. A model-side failure is a RESULT here, never an
///     exception: the caller has to record a decision, and a throw would leave the artifact stuck Pending with no
///     reason for an operator to act on.
/// </summary>
public sealed class TrainedModelSmokeGateTests
{
    private static readonly TrainingArtifactRecordView MergedArtifact = new("/staged/merged-Q4_K_M.gguf", BaseModelFilePath: null);

    private static readonly TrainingArtifactRecordView AdapterArtifact = new("/staged/adapter-F16.gguf", "/models/base.gguf");

    [Test]
    public async Task Smoke_WhenTheModelEmitsAValidToolCall_Passes()
    {
        var harness = Harness.Create(toolCall: ("get_weather", "Paris, France"));

        var result = await harness.Gate.RunAsync(MergedArtifact, CancellationToken.None);

        AssertEx.Equal(TrainingArtifactSmokeState.Passed, result.State);
        AssertEx.Null(result.Reason);
        AssertEx.Equal(MergedArtifact.ArtifactPath, harness.Request?.ModelFilePath);
        AssertEx.Null(harness.Request?.AdapterFilePath);
    }

    [Test]
    public async Task Smoke_ForAnAdapter_LoadsTheBaseModelWithTheAdapterOnTop()
    {
        // Exactly how a promoted adapter entry is served: -m base, --lora adapter. Testing the adapter file alone
        // would prove nothing about the pair that actually runs.
        var harness = Harness.Create(toolCall: ("get_weather", "Paris, France"));

        var result = await harness.Gate.RunAsync(AdapterArtifact, CancellationToken.None);

        AssertEx.Equal(TrainingArtifactSmokeState.Passed, result.State);
        AssertEx.Equal("/models/base.gguf", harness.Request?.ModelFilePath);
        AssertEx.Equal(AdapterArtifact.ArtifactPath, harness.Request?.AdapterFilePath);
    }

    [Test]
    public async Task Smoke_WhenTheModelProducesNoToolCall_Fails()
    {
        // The failure worth catching: fine-tuning is precisely the operation that can destroy tool calling while
        // leaving a file that loads perfectly, so a load-only check would pass the worst models.
        var harness = Harness.Create(toolCall: null);

        var result = await harness.Gate.RunAsync(MergedArtifact, CancellationToken.None);

        AssertEx.Equal(TrainingArtifactSmokeState.Failed, result.State);
        AssertEx.Contains(result.Reason ?? string.Empty, "no tool call", StringComparison.Ordinal);
    }

    [Test]
    public async Task Smoke_WhenTheToolCallHasNoArguments_Fails()
    {
        var harness = Harness.Create(toolCall: ("get_weather", null));

        var result = await harness.Gate.RunAsync(MergedArtifact, CancellationToken.None);

        AssertEx.Equal(TrainingArtifactSmokeState.Failed, result.State);
        AssertEx.Contains(result.Reason ?? string.Empty, "malformed", StringComparison.Ordinal);
    }

    [Test]
    public async Task Smoke_WhenTheLoadedModelHasNoChatTemplate_Fails()
    {
        // A merge that lost the tokenizer files answers /props without a template. Every tool call after that is
        // meaningless, so the cheap half of the check runs first.
        var harness = Harness.Create(toolCall: ("get_weather", "Paris, France"), chatTemplate: null);

        var result = await harness.Gate.RunAsync(MergedArtifact, CancellationToken.None);

        AssertEx.Equal(TrainingArtifactSmokeState.Failed, result.State);
        AssertEx.Contains(result.Reason ?? string.Empty, "chat template", StringComparison.Ordinal);
    }

    [Test]
    public async Task Smoke_WhenTheRuntimeCannotLoadTheFile_FailsWithTheRuntimesReason()
    {
        var harness = Harness.Create(toolCall: null, launchFailure: new LlamaRuntimeException("The model runtime exited while loading the model."));

        var result = await harness.Gate.RunAsync(MergedArtifact, CancellationToken.None);

        AssertEx.Equal(TrainingArtifactSmokeState.Failed, result.State);
        AssertEx.Contains(result.Reason ?? string.Empty, "exited while loading", StringComparison.Ordinal);
    }

    private sealed class Harness
    {
        private Harness()
        {
        }

        public ITrainedModelSmokeGate Gate { get; private set; } = null!;

        public TransientLlamaServerRequest? Request { get; private set; }

        public static Harness Create((string Name, string? Argument)? toolCall,
            string? chatTemplate = "{% for m in messages %}{{ m }}{% endfor %}",
            Exception? launchFailure = null)
        {
            var harness = new Harness();
            var launcher = new ScriptedTransientLauncher(request => harness.Request = request, launchFailure);

            var chatClientFactory = Substitute.For<IInferenceChatClientFactory>();
            _ = chatClientFactory.CreateChatClient(Arg.Any<Uri>(), Arg.Any<string>()).Returns(_ => new ScriptedChatClient(toolCall));

            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            _ = httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new PropsHandler(chatTemplate)));

            harness.Gate = new TrainedModelSmokeGate(launcher,
                chatClientFactory,
                httpClientFactory,
                new NoOpGpuModelLoadAdmission(),
                NullLogger<TrainedModelSmokeGate>.Instance);
            return harness;
        }
    }

    /// <summary>Runs the body against a fixed loopback session instead of a real process, or fails the launch.</summary>
    private sealed class ScriptedTransientLauncher(Action<TransientLlamaServerRequest> capture, Exception? failure) : ITransientLlamaServerLauncher
    {
        public Task<T> RunAsync<T>(TransientLlamaServerRequest request,
            Func<TransientLlamaServerSession, CancellationToken, Task<T>> body,
            CancellationToken ct)
        {
            capture(request);
            return failure is not null
                ? Task.FromException<T>(failure)
                : body(new TransientLlamaServerSession(new Uri("http://127.0.0.1:18080/v1"), Path.GetFileName(request.ModelFilePath)), ct);
        }
    }

    private sealed class PropsHandler(string? chatTemplate) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = chatTemplate is null ? "{}" : $$"""{"chat_template":{{JsonSerializer.Serialize(chatTemplate)}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ScriptedChatClient((string Name, string? Argument)? toolCall) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (toolCall is not { } call)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I do not know.")));
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (call.Argument is { } argument)
            {
                arguments["location"] = argument;
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call-1", call.Name, arguments)
            })));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unused");
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
