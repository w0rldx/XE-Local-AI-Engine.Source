namespace XE_Local_AI_Engine.AI.Agent.Tests.DependencyInjection;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The gen_ai OpenTelemetry pipeline must set <c>EnableSensitiveData</c> from the code-owned
///     <see cref="AgentTelemetryOptions" /> rather than defer to the ambient
///     <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c> environment variable (which Aspire injects as true).
/// </summary>
public sealed class AgentTelemetrySensitiveDataTests
{
    private const string GenAiCaptureEnvVar = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

    [Test]
    [NotInParallel(nameof(AgentTelemetrySensitiveDataTests))]
    public void Pipeline_WithEnvVarTrue_AndNoOptIn_LeavesSensitiveDataDisabled()
    {
        var previous = Environment.GetEnvironmentVariable(GenAiCaptureEnvVar);
        try
        {
            // The environment says "capture content"; the code-owned option is off. The explicit set must win.
            Environment.SetEnvironmentVariable(GenAiCaptureEnvVar, "true");

            var openTelemetryClient = BuildPipelineOpenTelemetryClient(captureSensitiveContent: false);

            AssertEx.False(openTelemetryClient.EnableSensitiveData,
                "the ambient capture env var must not enable sensitive-data capture when the code-owned option is off");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GenAiCaptureEnvVar, previous);
        }
    }

    [Test]
    [NotInParallel(nameof(AgentTelemetrySensitiveDataTests))]
    public void Pipeline_WithOptIn_EnablesSensitiveData()
    {
        var previous = Environment.GetEnvironmentVariable(GenAiCaptureEnvVar);
        try
        {
            // Even with the env var unset/false, the explicit opt-in enables capture.
            Environment.SetEnvironmentVariable(GenAiCaptureEnvVar, value: null);

            var openTelemetryClient = BuildPipelineOpenTelemetryClient(captureSensitiveContent: true);

            AssertEx.True(openTelemetryClient.EnableSensitiveData,
                "the code-owned opt-in must enable sensitive-data capture");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GenAiCaptureEnvVar, previous);
        }
    }

    private static OpenTelemetryChatClient BuildPipelineOpenTelemetryClient(bool captureSensitiveContent)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IChatClient>(_ => new FakeChatClient());
        services.AddOptions<AgentTelemetryOptions>().Configure(options => options.CaptureSensitiveContent = captureSensitiveContent);
        services.DecorateChatClientPipeline();

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IChatClient>();

        return pipeline.GetService(typeof(OpenTelemetryChatClient)) as OpenTelemetryChatClient
               ?? throw new AssertionException("Expected an OpenTelemetryChatClient in the pipeline.");
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
