namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IMemoryExtractionAgent" />, running a <b>node-local</b> model and forcing a structured JSON
///     response so each candidate carries its scope and confidence.
/// </summary>
/// <remarks>
///     The model is resolved per-model through <see cref="ILocalModelProviderResolver" />, never the shared
///     <see cref="IChatClient" /> singleton that can be a cloud client, so the run's turns and answer never cross the
///     node boundary. Tests substitute a fake agent, mirroring the analysis seam, so CI needs no model runtime.
/// </remarks>
internal sealed class DefaultMemoryExtractionAgent : IMemoryExtractionAgent
{
    // A structured answer of a few short candidates needs a few hundred tokens; the cap only stops a runaway generation
    // from holding the background slot to the window.
    private const int MaxOutputTokens = 1024;

    // The most run text the prompt carries, the fold/distill ceiling (MaxInputCharsPerSummarizationCall's default). Each run is also
    // bounded so the whole prompt and MaxOutputTokens fit the background slot's headroom at the model's calibrated characters per token.
    internal const int MaxRunInputChars = 12_000;

    // The JSON keys, quotes and separators around the run's text. Accepted ceiling: escaped quotes and line breaks are not charged.
    private const int PromptFrameChars = 256;

    // A sanitized error is a sentence or a stack head; its first characters say what failed.
    private const int MaxErrorChars = 1_000;

    // Relaxed escaping, as the summarizer's: the default encoder writes six characters per non-Latin rune, which the budget would not see.
    // Safe because the body is only ever sent to the model, never rendered.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ITokenEstimatorCalibrationStore _calibrationStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DefaultMemoryExtractionAgent> _logger;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly MemoryExtractionOptions _options;

    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly INodeRuntimeSettings _runtimeSettings;

    public DefaultMemoryExtractionAgent(ILocalModelProviderResolver providerResolver,
        IOptions<MemoryExtractionOptions> options,
        INodeRuntimeSettings runtimeSettings,
        IModelTrustResolver modelTrustResolver,
        IServiceScopeFactory scopeFactory,
        ITokenEstimatorCalibrationStore calibrationStore,
        ILogger<DefaultMemoryExtractionAgent> logger)
    {
        _calibrationStore = calibrationStore ?? throw new ArgumentNullException(nameof(calibrationStore));
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(providerResolver);
        _providerResolver = providerResolver;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<IReadOnlyList<ProposedMemory>> ProposeAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Disabled gate: with no node-local extraction model there is no model call and no candidate. The service
        // checks this too, but the agent owns the privacy-critical call, so it guards independently.
        var modelName = await _runtimeSettings.GetMemoryExtractionModelNameAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(modelName)
            || !await BackgroundModelLocalityGuard.AllowAsync(modelName, "MemoryExtractionModelName", _modelTrustResolver, _logger, cancellationToken))
        {
            return [];
        }

        // Route the extraction model to the runtime that serves it, node-local only and never the cloud singleton.
        // THIS resolution IS the privacy invariant: conversation content only ever reaches a per-provider client.
        var provider = await _providerResolver.ResolveProviderForModelAsync(modelName, cancellationToken);
        var selection = new LocalModelSelection
        {
            ModelName = modelName,
            ProviderName = provider.ProviderName,
            ResidencyIntent = ModelResidencyIntent.Background
        };

        // IChatClient is IDisposable — dispose the per-run node-local client.
        using var chatClient = provider.CreateChatClient(selection).WithProviderTelemetry();

        var systemPrompt = BuildSystemPrompt(_options.MaxCandidates, run.Failed);
        var headroomChars = LlamaServerChatSlots.BackgroundPromptChars(MaxOutputTokens, _calibrationStore.ResolveDivisor(modelName));
        var runChars = (int)Math.Clamp(headroomChars - systemPrompt.Length - PromptFrameChars, 0, MaxRunInputChars);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, JsonSerializer.Serialize(ToPromptModel(run, runChars), SerializerOptions))
        ];

        var chatOptions = new ChatOptions
        {
            Temperature = 0f,
            MaxOutputTokens = MaxOutputTokens
        };

        // Reasoning OFF, both halves at once and only on a thinking-capable model, exactly as the summarizer sends it.
        // The capability resolver is scoped and this agent a singleton, so it is resolved in a scope of its own.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var capabilities = await scope.ServiceProvider.GetRequiredService<IModelCapabilityResolver>().ResolveAsync(modelName, cancellationToken);
        if (capabilities.SupportsThinking)
        {
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = false,
                [InvocationAgentFactory.LlamaDisableThinkingMarkerKey] = true
            };
        }

        var response = await chatClient
            .GetResponseAsync<ExtractionEnvelope>(messages, chatOptions, cancellationToken: cancellationToken);

        if (!response.TryGetResult(out var envelope) || envelope?.Memories is null)
        {
            _logger.LogWarning("Memory extraction model returned no parseable candidates.");
            return [];
        }

        // A non-failed run can never yield a Failure-scope memory — the model is told this, but enforce it here too so a
        // confused model cannot mislabel a successful run as a lesson about a failure.
        return [.. envelope.Memories.Select(ToProposedMemory).Where(candidate => run.Failed || candidate.Scope != MemoryScope.Failure)];
    }

    private static ProposedMemory ToProposedMemory(ExtractionCandidate candidate)
    {
        // Pass the raw candidate through; the service validates/dedupes and the store stamps Suggested/Extracted.
        return new ProposedMemory
        {
            Behavior = candidate.Behavior ?? string.Empty,
            Scope = MapScope(candidate.Scope),
            TriggerCondition = candidate.TriggerCondition,
            Confidence = candidate.Confidence
        };
    }

    private static MemoryScope MapScope(string? scope)
    {
        return scope?.Trim().ToUpperInvariant() switch
        {
            "FAILURE" => MemoryScope.Failure,
            "USERPREFERENCE" or "USER_PREFERENCE" or "PREFERENCE" => MemoryScope.UserPreference,
            "PROJECT" => MemoryScope.Project,
            // Procedural is the safe default for an unknown/blank scope (a how-to lesson is the most common, lowest-risk
            // kind to stage for review).
            _ => MemoryScope.Procedural
        };
    }

    private static object ToPromptModel(MemoryExtractionRunInput run, int maxChars)
    {
        // Hand the model only what it needs to distill a durable lesson: the user turns, the assistant's answer, and
        // whether the run failed (+ the sanitized error string for a failed run). No ids — the model never needs them.
        var fitted = FitRunInput(run, maxChars);
        return new
        {
            fitted.UserTurns,
            AssistantResponse = fitted.Answer,
            run.Failed,
            fitted.Error
        };
    }

    /// <summary>
    ///     The run's text bounded to <paramref name="maxChars" />: the error's head, the answer's tail, and the newest user turns.
    /// </summary>
    /// <remarks>
    ///     The answer keeps its END because that is where its outcome is (the conclusion, or the last step before a failure), and it gets
    ///     at least half the budget; the user turns fill the rest newest first, because the newest turns state the task the run
    ///     answered and older ones only lead up to it. A turn that does not fit whole keeps its end too.
    /// </remarks>
    internal static FittedRunInput FitRunInput(MemoryExtractionRunInput run, int maxChars)
    {
        var error = run.Failed && run.Error is { } failure ? failure[..Math.Min(failure.Length, Math.Min(MaxErrorChars, Math.Max(0, maxChars)))] : null;
        var budget = maxChars - (error?.Length ?? 0);
        var userChars = run.UserTurns.Sum(static turn => turn.Content.Length);
        var answer = KeepTail(run.AssistantResponse, Math.Max(budget / 2, budget - userChars));

        var room = budget - answer.Length;
        var kept = new List<string>();
        for (var index = run.UserTurns.Count - 1; index >= 0 && room > 0; index--)
        {
            var turn = KeepTail(run.UserTurns[index].Content, room);
            kept.Insert(0, turn);
            room -= turn.Length;
        }

        return new FittedRunInput
        {
            UserTurns = [.. kept],
            Answer = answer,
            Error = error
        };
    }

    internal sealed class FittedRunInput
    {
        public required string[] UserTurns { get; init; }

        public required string Answer { get; init; }

        public required string? Error { get; init; }
    }

    // Never starts on the low half of a surrogate pair, so the cut cannot produce an unpaired surrogate.
    private static string KeepTail(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var start = text.Length - Math.Max(0, maxChars);
        return text[(start < text.Length && char.IsLowSurrogate(text[start]) ? start + 1 : start)..];
    }

    private static string BuildSystemPrompt(int maxCandidates, bool failed)
    {
        var failureLine = failed
            ? "This run FAILED — prefer a \"failure\" memory capturing what to avoid next time."
            : "This run SUCCEEDED — do NOT emit a \"failure\" memory.";

        return $$"""
                 You distill durable, reusable lessons from one completed AI-agent run so the agent improves over time.
                 You are given a JSON object: the user's turns, the assistant's final answer, and a failure flag.

                 Propose at most {{maxCandidates}} memories. Return ONLY a JSON object of the form:
                 { "memories": [ { "behavior": string, "scope": string, "triggerCondition": string|null,
                   "confidence": number } ] }

                 Rules:
                 - "behavior" is a single concrete, generalizable instruction to add to the agent's playbook — NOT a
                   restatement of this conversation. If nothing durable was learned, return { "memories": [] }.
                 - "scope" is one of: "procedural" (a how-to/procedure), "failure" (what to avoid), "userPreference"
                   (a stated user preference), "project" (a project-specific fact/convention).
                 - {{failureLine}}
                 - "triggerCondition" is an optional short phrase describing when the memory applies, or null.
                 - "confidence" is a number between 0 and 1.
                 - Do NOT include any personal data, secrets, or verbatim conversation text in "behavior".
                 """;
    }

    // Positional records: System.Text.Json binds JSON properties to the constructor parameters by name (Web defaults),
    // and the constructor counts as the assignment so the unassigned-auto-property analyzer stays quiet.
    private sealed record ExtractionEnvelope(List<ExtractionCandidate>? Memories);

    private sealed record ExtractionCandidate(
        string? Behavior,
        string? Scope,
        string? TriggerCondition,
        double Confidence);
}
