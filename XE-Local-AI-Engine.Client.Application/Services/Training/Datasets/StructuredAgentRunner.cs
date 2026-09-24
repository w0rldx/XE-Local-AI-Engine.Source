namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

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
///     Structured-output teacher runner: a threadless <see cref="ChatClientAgent" />, no ctor instructions, a single
///     leading system seed message.
/// </summary>
/// <remarks>
///     Modeled on <c>MafPlaybookEvalAgentRunner</c>'s shape but a separate type, because that runner hard-codes an
///     empty tool set, has no response-format parameter, and its eval-gate contract depends on both.
///     <see cref="TeacherOutputMode.Constrained" /> sets <see cref="ChatOptions.ResponseFormat" /> — the llama.cpp
///     provider already forwards the json-schema variant verbatim, so no raw-body patch is needed here — while
///     <see cref="TeacherOutputMode.ValidateAfter" /> sets none and leaves post-hoc validation to the pipeline.
/// </remarks>
public sealed class StructuredAgentRunner : IStructuredAgentRunner
{
    private readonly TimeSpan _turnTimeout = TurnTimeout;

    public StructuredAgentRunner(IModelCapabilityResolver capabilityResolver,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(capabilityResolver);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _capabilityResolver = capabilityResolver;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
    }

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

    /// <summary>Upper bound for one teacher turn.</summary>
    /// <remarks>
    ///     Live-found: a non-streaming completion to llama-server that never came back parked the whole generation
    ///     queue with the server reporting every slot idle, and nothing upstream carries a deadline, so this seam
    ///     owns it. A turn that overruns is that sample's failure, never the run's, and never a wedge.
    /// </remarks>
    internal static readonly TimeSpan TurnTimeout = TrainingAiClientPolicy.TurnTimeout;

    private readonly IModelCapabilityResolver _capabilityResolver;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;

    public async Task<StructuredAgentResult> RunAsync(IChatClient chatClient,
        StructuredAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        TrainingModelEligibility.EnsureNotExternal(request.ModelName, "dataset generation teachers");

        var (supportsThinking, _, isCloud) = await _capabilityResolver.ResolveAsync(request.ModelName, cancellationToken);
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
        // headless executor is the only thing that ever executes one. Live tools here would execute outside the approval gate.
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

        var chatOptions = TrainingAiClientPolicy.CreateOptions(request.ModelName, request.Temperature);
        if (TryParseSeed(request.Seed, out var seedValue))
        {
            chatOptions.Seed = seedValue;
        }

        if (request.OutputMode == TeacherOutputMode.Constrained)
        {
            chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(request.ResponseSchema, "teacher_sample", "one generated training sample");
        }

        using var activity = TrainingAiClientPolicy.StartActivity("dataset-generation");
        using var turnCancellation = TrainingAiClientPolicy.CreateTurnCancellation(cancellationToken, _turnTimeout);
        try
        {
            var response = await agent.RunAsync(seed, session: null, new ChatClientAgentRunOptions
            {
                ChatOptions = chatOptions
            }, turnCancellation.Token);
            activity?.SetStatus(ActivityStatusCode.Ok);
            var text = response.Text ?? string.Empty;
            return string.IsNullOrWhiteSpace(text)
                ? new StructuredAgentResult
                {
                    Success = false,
                    Text = string.Empty,
                    FailureReason = "The teacher returned an empty completion."
                }
                : new StructuredAgentResult
                {
                    Success = true,
                    Text = text,
                    FailureReason = null
                };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The turn deadline fired, not the caller: a per-sample failure with a reason the operator can act on.
            return new StructuredAgentResult
            {
                Success = false,
                Text = string.Empty,
                FailureReason = $"The teacher did not answer within {_turnTimeout.TotalSeconds:0} seconds."
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TrainingValidationException)
        {
            // One turn's transport/model failure is a per-sample failure, never the run's.
            return new StructuredAgentResult
            {
                Success = false,
                Text = string.Empty,
                FailureReason = TrainingAiClientPolicy.TranslateProviderFailure(activity, exception)
            };
        }
    }

    private static bool TryParseSeed(string? seed, out long value)
    {
        // -1 is the runtime's "random seed" sentinel; anything below it is invalid, so it is skipped rather than sent.
        value = 0;
        return !string.IsNullOrWhiteSpace(seed) && long.TryParse(seed, out value) && value >= -1;
    }
}
