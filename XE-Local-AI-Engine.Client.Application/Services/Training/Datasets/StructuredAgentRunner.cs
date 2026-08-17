namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>One teacher turn. The seed is carried as a string for the same 2^53 precision reason the sampling DTO uses.</summary>
public sealed record StructuredAgentRequest(
    string ModelName,
    string SystemInstructions,
    string UserPrompt,
    TeacherOutputMode OutputMode,
    JsonElement ResponseSchema,
    float Temperature,
    string? Seed);

public sealed record StructuredAgentResult(bool Success, string Text, string? FailureReason);

public interface IStructuredAgentRunner
{
    /// <summary>
    ///     Runs one teacher turn against a caller-owned node-local client. Throws
    ///     <see cref="TrainingValidationException" /> when the definition itself is incompatible with the model (a
    ///     reasoning teacher in <see cref="TeacherOutputMode.Constrained" />); a per-turn problem comes back as a failed
    ///     <see cref="StructuredAgentResult" /> instead.
    /// </summary>
    Task<StructuredAgentResult> RunAsync(IChatClient chatClient, StructuredAgentRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
///     Structured-output teacher runner. Modeled on <c>MafPlaybookEvalAgentRunner</c>'s shape — threadless
///     <see cref="ChatClientAgent" />, no ctor instructions, a single leading system seed message — but it is a separate
///     type: that runner hard-codes an empty tool set and has no response-format parameter, and its eval-gate contract
///     depends on both.
///     <para>
///         <see cref="TeacherOutputMode.Constrained" /> sets <see cref="ChatOptions.ResponseFormat" />; the llama.cpp
///         provider already forwards the json-schema variant verbatim, so no raw-body patch is needed here.
///         <see cref="TeacherOutputMode.ValidateAfter" /> sets none and leaves post-hoc validation to the pipeline.
///     </para>
/// </summary>
public sealed class StructuredAgentRunner(
    IModelCapabilityResolver capabilityResolver,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider) : IStructuredAgentRunner
{
    private readonly TimeSpan _turnTimeout = TurnTimeout;

    /// <summary>Test seam: the same runner with a caller-chosen per-turn deadline.</summary>
    internal StructuredAgentRunner(IModelCapabilityResolver capabilityResolver,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        TimeSpan turnTimeout) : this(capabilityResolver, loggerFactory, serviceProvider)
    {
        _turnTimeout = turnTimeout;
    }

    private const string AgentName = "dataset-teacher";
    private const string AgentDescription = "Training dataset generation teacher.";

    /// <summary>
    ///     Upper bound for one teacher turn. Live-found (2026-08-15): a non-streaming completion to llama-server that
    ///     never came back parked the whole generation queue with the server reporting every slot idle — nothing
    ///     upstream carries a deadline, so this seam owns it. A turn that overruns is that sample's failure, never
    ///     the run's, and never a wedge.
    /// </summary>
    internal static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(5);

    private readonly IModelCapabilityResolver _capabilityResolver = capabilityResolver ?? throw new ArgumentNullException(nameof(capabilityResolver));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public async Task<StructuredAgentResult> RunAsync(IChatClient chatClient,
        StructuredAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var (supportsThinking, _, isCloud) = await _capabilityResolver.ResolveAsync(request.ModelName, cancellationToken).ConfigureAwait(false);
        if (isCloud)
        {
            // Invariant #5: teacher, critic and judge are node-local.
            throw new TrainingValidationException($"'{request.ModelName}' is a cloud model; dataset generation teachers must be node-local.");
        }

        if (request.OutputMode == TeacherOutputMode.Constrained && supportsThinking)
        {
            // A reasoning model emits its thinking outside the constrained grammar, so constrained decoding cannot hold
            // for the whole completion. ValidateAfter is the supported mode for these teachers (decision #15).
            throw new TrainingValidationException($"'{request.ModelName}' is a reasoning model and cannot be used in Constrained mode; use ValidateAfter.");
        }

        // No tools are offered to the teacher: it DESCRIBES the call it would make inside the structured record, and the
        // headless executor is the only thing that ever executes one. Handing it live tools would execute them here,
        // outside the approval gate.
        var agent = new ChatClientAgent(chatClient,
            instructions: null,
            name: AgentName,
            description: AgentDescription,
            tools: new List<AITool>(),
            loggerFactory: _loggerFactory,
            services: _serviceProvider);

        List<ChatMessage> seed =
        [
            new(ChatRole.System, request.SystemInstructions),
            new(ChatRole.User, request.UserPrompt)
        ];

        var chatOptions = new ChatOptions
        {
            ModelId = request.ModelName,
            Temperature = request.Temperature
        };
        if (TryParseSeed(request.Seed, out var seedValue))
        {
            chatOptions.Seed = seedValue;
        }

        if (request.OutputMode == TeacherOutputMode.Constrained)
        {
            chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(request.ResponseSchema, "teacher_sample", "one generated training sample");
        }

        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnCancellation.CancelAfter(_turnTimeout);
        try
        {
            var response = await agent.RunAsync(seed, session: null, new ChatClientAgentRunOptions
                                      {
                                          ChatOptions = chatOptions
                                      }, turnCancellation.Token)
                                      .ConfigureAwait(false);
            var text = response.Text ?? string.Empty;
            return string.IsNullOrWhiteSpace(text)
                ? new StructuredAgentResult(Success: false, string.Empty, "The teacher returned an empty completion.")
                : new StructuredAgentResult(Success: true, text, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The turn deadline fired, not the caller: a per-sample failure with a reason the operator can act on.
            return new StructuredAgentResult(Success: false, string.Empty,
                $"The teacher did not answer within {_turnTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TrainingValidationException)
        {
            // One turn's transport/model failure is a per-sample failure, never the run's.
            return new StructuredAgentResult(Success: false, string.Empty, exception.Message);
        }
    }

    private static bool TryParseSeed(string? seed, out long value)
    {
        // -1 is the runtime's "random seed" sentinel; anything below it is invalid, so it is skipped rather than sent.
        value = 0;
        return !string.IsNullOrWhiteSpace(seed) && long.TryParse(seed, out value) && value >= -1;
    }
}
