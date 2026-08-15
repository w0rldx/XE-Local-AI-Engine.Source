namespace XE_Local_AI_Engine.Tests.Training;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class StructuredAgentRunnerTests
{
    private const string SampleJson = """{"userMessage":"hi","assistantText":"there"}""";

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"userMessage":{"type":"string"}},"required":["userMessage"]}""").RootElement.Clone();

    [Test]
    public async Task StructuredOutput_ReasoningTeacher_RejectedInConstrainedMode()
    {
        using var client = new RecordingChatClient(SampleJson);
        var runner = CreateRunner(supportsThinking: true);

        var exception = await AssertEx.ThrowsAsync<TrainingValidationException>(
            () => runner.RunAsync(client, Request(TeacherOutputMode.Constrained)));

        AssertEx.Contains(exception.Message, "Constrained");
        AssertEx.False(client.WasCalled, "A rejected teacher must never reach the model.");
    }

    [Test]
    public async Task StructuredOutput_ReasoningTeacher_AdmittedInValidateAfterMode()
    {
        using var client = new RecordingChatClient(SampleJson);
        var runner = CreateRunner(supportsThinking: true);

        var result = await runner.RunAsync(client, Request(TeacherOutputMode.ValidateAfter));

        AssertEx.True(result.Success, "ValidateAfter admits a reasoning teacher and validates post-hoc.");
        AssertEx.True(client.WasCalled, "The teacher turn should have reached the model.");
        AssertEx.Null(client.LastOptions?.ResponseFormat, "ValidateAfter must not set a response format.");
    }

    [Test]
    public async Task ConstrainedMode_NonReasoningTeacher_SetsTheJsonSchemaResponseFormat()
    {
        using var client = new RecordingChatClient(SampleJson);
        var runner = CreateRunner(supportsThinking: false);

        var result = await runner.RunAsync(client, Request(TeacherOutputMode.Constrained) with
        {
            Seed = "41"
        });

        AssertEx.True(result.Success, "A non-reasoning teacher is admitted in Constrained mode.");
        _ = AssertEx.NotNull(client.LastOptions?.ResponseFormat as ChatResponseFormatJson, "Constrained mode forwards a json-schema response format.");
        AssertEx.Equal(expected: 41L, client.LastOptions!.Seed!.Value);
    }

    [Test]
    public async Task CloudTeacher_IsRejected_BecauseTeachersAreNodeLocal()
    {
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        _ = capabilities.ResolveAsync("teacher.gguf", Arg.Any<CancellationToken>()).Returns((false, true, true));
        var runner = new StructuredAgentRunner(capabilities, NullLoggerFactory.Instance, new EmptyServiceProvider());
        using var client = new RecordingChatClient(SampleJson);

        var exception = await AssertEx.ThrowsAsync<TrainingValidationException>(() => runner.RunAsync(client, Request(TeacherOutputMode.ValidateAfter)));

        AssertEx.Contains(exception.Message, "cloud model");
    }

    private static StructuredAgentRunner CreateRunner(bool supportsThinking)
    {
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        _ = capabilities.ResolveAsync("teacher.gguf", Arg.Any<CancellationToken>()).Returns((supportsThinking, true, false));
        return new StructuredAgentRunner(capabilities, NullLoggerFactory.Instance, new EmptyServiceProvider());
    }

    private static StructuredAgentRequest Request(TeacherOutputMode mode) =>
        new("teacher.gguf", "system", "produce one example", mode, Schema, Temperature: 0.2f, Seed: null);

    /// <summary>Minimal node-local client stand-in that records the options the runner composed.</summary>
    private sealed class RecordingChatClient(string responseText) : IChatClient
    {
        public bool WasCalled { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
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

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            null;
    }
}
