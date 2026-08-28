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

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"userMessage":{"type":"string"}},"required":["userMessage"]}""").RootElement.Clone();

    [Test]
    public async Task StructuredOutput_ReasoningTeacher_RejectedInConstrainedMode()
    {
        using var client = new RecordingChatClient(SampleJson);
        var runner = CreateRunner(supportsThinking: true);

        var exception = await AssertEx.ThrowsAsync<TrainingValidationException>(() => runner.RunAsync(client, Request(TeacherOutputMode.Constrained)));

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
        AssertEx.Equal(expected: 2048, client.LastOptions.MaxOutputTokens!.Value,
            "Non-interactive training turns must carry the shared output budget.");
    }

    [Test]
    public async Task ProviderFailure_IsSanitizedBeforeItLeavesTheTrainingPolicyBoundary()
    {
        const string secret = "Authorization: Bearer provider-secret";
        using var client = new ThrowingChatClient(new HttpRequestException(secret));
        var runner = CreateRunner(supportsThinking: false);

        var result = await runner.RunAsync(client, Request(TeacherOutputMode.ValidateAfter));

        AssertEx.False(result.Success);
        AssertEx.Equal("The local model provider could not complete this sample.", result.FailureReason!);
        AssertEx.False(result.FailureReason!.Contains(secret, StringComparison.Ordinal), "Raw provider messages must never enter persisted rejection reasons or hub events.");
    }

    [Test]
    public async Task CloudTeacher_IsRejected_BecauseTeachersAreNodeLocal()
    {
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        _ = capabilities.ResolveAsync("teacher.gguf", Arg.Any<CancellationToken>()).Returns(new ModelCapabilitySnapshot(false, true, true));
        var runner = new StructuredAgentRunner(capabilities, NullLoggerFactory.Instance, new EmptyServiceProvider());
        using var client = new RecordingChatClient(SampleJson);

        var exception = await AssertEx.ThrowsAsync<TrainingValidationException>(() => runner.RunAsync(client, Request(TeacherOutputMode.ValidateAfter)));

        AssertEx.Contains(exception.Message, "cloud model");
    }

    [Test]
    public async Task TeacherTurn_ThatNeverAnswers_FailsTheSampleAtTheDeadline_NotTheRun()
    {
        // Live-found: a lost non-streaming completion parked the generation queue with llama-server fully idle.
        using var client = new HangingChatClient();
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        _ = capabilities.ResolveAsync("teacher.gguf", Arg.Any<CancellationToken>()).Returns(new ModelCapabilitySnapshot(false, true, false));
        var runner = new StructuredAgentRunner(capabilities, NullLoggerFactory.Instance, new EmptyServiceProvider(), TimeSpan.FromMilliseconds(200));

        var result = await runner.RunAsync(client, Request(TeacherOutputMode.Constrained));

        AssertEx.False(result.Success, "A turn that overruns its deadline is a per-sample failure.");
        AssertEx.True(result.FailureReason?.Contains("did not answer", StringComparison.Ordinal) == true, "The reason names the deadline.");
        AssertEx.True(client.SawCancellation, "The deadline must cancel the underlying model call so it cannot leak.");
    }

    [Test]
    public async Task TeacherTurn_CallerCancellation_StillPropagatesAsCancellation()
    {
        using var client = new HangingChatClient();
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var runner = CreateRunner(supportsThinking: false);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(client, Request(TeacherOutputMode.Constrained), callerCancellation.Token));
    }

    private static StructuredAgentRunner CreateRunner(bool supportsThinking)
    {
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        _ = capabilities.ResolveAsync("teacher.gguf", Arg.Any<CancellationToken>()).Returns(new ModelCapabilitySnapshot(supportsThinking, true, false));
        return new StructuredAgentRunner(capabilities, NullLoggerFactory.Instance, new EmptyServiceProvider());
    }

    [Test]
    [Arguments("ext:local-box/qwen3")]
    [Arguments("ext:cloud-box/qwen3")]
    public async Task ExternalTeacher_IsRejected_WhateverItsDeclaredLocality(string teacherModel)
    {
        // The cloud refusal above does NOT cover a declared-LOCAL external model: the capability resolver reports it
        // node-local, which is right for the tool gates and wrong here. Training needs a runtime the node OWNS — an
        // endpoint it neither launched nor versioned can change model or sampling mid-run, leaving a dataset whose
        // provenance claims a determinism it never had.
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        _ = capabilities.ResolveAsync(teacherModel, Arg.Any<CancellationToken>())
                        .Returns(new ModelCapabilitySnapshot(false, true, false));
        var runner = new StructuredAgentRunner(capabilities, NullLoggerFactory.Instance, new EmptyServiceProvider());
        using var client = new RecordingChatClient(SampleJson);

        var exception = await AssertEx.ThrowsAsync<TrainingValidationException>(() =>
            runner.RunAsync(client, Request(TeacherOutputMode.ValidateAfter) with { ModelName = teacherModel }));

        AssertEx.Contains(exception.Message, "external model");
        AssertEx.False(client.WasCalled, "A refused teacher must never reach the transport.");
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

    /// <summary>A model that never answers — the shape of the live stall this seam's deadline exists for.</summary>
    private sealed class HangingChatClient : IChatClient
    {
        public bool SawCancellation { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            throw new InvalidOperationException("unreachable");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
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

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(exception);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

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
