namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The one gate between a staged export and the model registry.
/// </summary>
/// <remarks>
///     <para>
///         It answers two questions a digest cannot: does <c>llama-server</c> actually load this file, and does the
///         model still emit a syntactically valid tool call afterwards. Fine-tuning is exactly the operation that can
///         destroy tool-calling while leaving a file that loads perfectly, so a load-only check would pass the models
///         most worth catching.
///     </para>
///     <para>
///         This is Training's ONLY use of <see cref="IGpuModelLoadAdmission" />: a run holds the GPU through its own
///         exclusivity and never takes the load gate, but the smoke launch is an ordinary short GPU load and
///         serializes against every other one exactly like a chat spawn.
///     </para>
///     <para>
///         A model-side failure is a RESULT, never an exception — "this artifact cannot serve" is the verdict the
///         caller asked for, and it is recorded on the artifact so the operator sees the reason next to the file.
///     </para>
/// </remarks>
public sealed class TrainedModelSmokeGate(
    ITransientLlamaServerLauncher launcher,
    IInferenceChatClientFactory chatClientFactory,
    IHttpClientFactory httpClientFactory,
    IGpuModelLoadAdmission loadAdmission,
    ILogger<TrainedModelSmokeGate> logger) : ITrainedModelSmokeGate
{
    /// <summary>Small on purpose: the check proves the model serves, and a large window would only slow the load.</summary>
    private const int SmokeContextTokens = 2048;

    private const string ToolName = "get_weather";
    private const string ToolArgumentName = "location";

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);

    private readonly IInferenceChatClientFactory _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly ITransientLlamaServerLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly IGpuModelLoadAdmission _loadAdmission = loadAdmission ?? throw new ArgumentNullException(nameof(loadAdmission));
    private readonly ILogger<TrainedModelSmokeGate> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<TrainedModelSmokeResult> RunAsync(TrainingArtifactRecordView artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        // An adapter has no weights of its own: llama-server loads the installed BASE model and applies the staged
        // adapter on top, which is exactly how a promoted adapter entry would later be served.
        var modelPath = artifact.BaseModelFilePath ?? artifact.ArtifactPath;
        var adapterPath = artifact.BaseModelFilePath is null ? null : artifact.ArtifactPath;
        var request = new TransientLlamaServerRequest(modelPath, adapterPath, SmokeContextTokens, ReadinessTimeout);

        try
        {
            using var admission = await _loadAdmission.AcquireAsync(cancellationToken).ConfigureAwait(false);
            return await _launcher.RunAsync(request, ProbeAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is LlamaRuntimeException or GpuModelLoadAdmissionTimeoutException)
        {
            _logger.LogWarning(exception, "The smoke test could not load the staged artifact.");
            return Failed($"the model runtime could not load it: {exception.Message}");
        }
        catch (Exception exception)
        {
            // Anything else is still the artifact's verdict rather than a host fault: the caller has to record a
            // decision, and a throw here would leave the artifact stuck Pending with no reason at all.
            _logger.LogError(exception, "The smoke test failed unexpectedly.");
            return Failed("the smoke test did not complete");
        }
    }

    private async Task<TrainedModelSmokeResult> ProbeAsync(TransientLlamaServerSession session, CancellationToken cancellationToken)
    {
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnCancellation.CancelAfter(TurnTimeout);
        var token = turnCancellation.Token;

        // The template read-back is first because it is the cheap half: a fine-tune whose tokenizer files did not
        // survive the merge answers /props without a chat template, and every tool call after that is meaningless.
        if (!await HasChatTemplateAsync(session, token).ConfigureAwait(false))
        {
            return Failed("the loaded model reports no chat template, so it cannot serve chat or tool calls");
        }

        using var chatClient = _chatClientFactory.CreateChatClient(session.BaseAddress, session.ModelId);
        var tool = AIFunctionFactory.Create((string location) => $"22C in {location}",
            ToolName,
            "Get the current weather for a location.");
        var options = new ChatOptions
        {
            ModelId = session.ModelId,
            // Deterministic on purpose: the gate must give the same verdict twice for the same file, or a re-run
            // becomes a coin flip and nobody can trust either answer.
            Temperature = 0,
            Seed = 1,
            Tools = [tool],
            ToolMode = ChatToolMode.RequireAny
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant. Use the supplied tool when it answers the question."),
            new(ChatRole.User, "What is the weather in Paris, France?")
        };

        ChatResponse response;
        try
        {
            // NOT the function-invoking client: the gate is asking whether the model EMITS a well-formed call, and
            // letting the middleware run the tool would hide a malformed call behind a successful round trip.
            response = await chatClient.GetResponseAsync(messages, options, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed($"the tool-call request failed: {exception.GetType().Name}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("the tool-call request did not finish within its time limit");
        }

        var call = response.Messages
                           .SelectMany(static message => message.Contents)
                           .OfType<FunctionCallContent>()
                           .FirstOrDefault();
        if (call is null)
        {
            return Failed("the model produced no tool call for a prompt that requires one");
        }

        return string.Equals(call.Name, ToolName, StringComparison.Ordinal) && HasArgument(call)
            ? new TrainedModelSmokeResult(TrainingArtifactSmokeState.Passed, Reason: null)
            : Failed($"the model emitted a malformed tool call (name '{call.Name}')");
    }

    /// <summary>
    ///     Reads <c>/props</c> and confirms the server resolved a chat template. Anything unreadable is a failure:
    ///     the gate exists to be sceptical, and "could not tell" is not a pass.
    /// </summary>
    private async Task<bool> HasChatTemplateAsync(TransientLlamaServerSession session, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(new Uri(session.BaseAddress, "/props"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("chat_template", out var template)
                   && template.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(template.GetString());
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            _logger.LogDebug(exception, "The smoke test could not read /props from the transient server.");
            return false;
        }
    }

    private static bool HasArgument(FunctionCallContent call) =>
        call.Arguments is { Count: > 0 } arguments
        && arguments.TryGetValue(ToolArgumentName, out var value)
        && value?.ToString() is { Length: > 0 };

    private static TrainedModelSmokeResult Failed(string reason) =>
        new(TrainingArtifactSmokeState.Failed, $"The smoke test failed: {reason}.");
}
