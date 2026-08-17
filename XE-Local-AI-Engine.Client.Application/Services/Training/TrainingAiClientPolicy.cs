namespace XE_Local_AI_Engine.Client.Services.Training;

using System.Diagnostics;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     Shared policy for node-local, non-interactive training model calls. It owns the common deadline, output budget,
///     low-cardinality telemetry and provider-error translation. It never installs function-invocation middleware;
///     evaluation callers may attach declaration-only tool metadata, while dataset generation attaches no tools.
/// </summary>
internal static class TrainingAiClientPolicy
{
    internal const int MaxOutputTokens = 2048;
    internal const string ProviderFailureReason = "The local model provider could not complete this sample.";
    internal static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(5);

    public static ChatOptions CreateOptions(string modelName, float temperature, IList<AITool>? declaredTools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return new ChatOptions
        {
            ModelId = modelName,
            Temperature = temperature,
            MaxOutputTokens = MaxOutputTokens,
            Tools = declaredTools
        };
    }

    public static CancellationTokenSource CreateTurnCancellation(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout ?? TurnTimeout);
        return source;
    }

    public static Activity? StartActivity(string operation)
    {
        var activity = NodeActivitySource.Source.StartActivity("training.ai.turn", ActivityKind.Internal);
        _ = activity?.SetTag("training.operation", operation);
        _ = activity?.SetTag("gen_ai.request.max_tokens", MaxOutputTokens);
        return activity;
    }

    public static string TranslateProviderFailure(Activity? activity, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _ = activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetStatus(ActivityStatusCode.Error, ProviderFailureReason);
        return ProviderFailureReason;
    }
}
