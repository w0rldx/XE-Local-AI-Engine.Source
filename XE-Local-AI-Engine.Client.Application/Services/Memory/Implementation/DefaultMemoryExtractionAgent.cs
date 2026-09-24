namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Default <see cref="IMemoryExtractionAgent" />, running a <b>node-local</b> model and forcing a structured JSON
///     response so each candidate carries its scope and confidence.
/// </summary>
/// <remarks>
///     The model is resolved per-model through <see cref="ILocalModelProviderResolver" />, never the shared
///     <see cref="IChatClient" /> singleton that can be a cloud client, so the run's turns and answer never cross the
///     node boundary. Tests substitute a fake agent, mirroring the analysis seam, so CI needs no Ollama.
/// </remarks>
internal sealed class DefaultMemoryExtractionAgent : IMemoryExtractionAgent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<DefaultMemoryExtractionAgent> _logger;
    private readonly MemoryExtractionOptions _options;

    private readonly ILocalModelProviderResolver _providerResolver;

    public DefaultMemoryExtractionAgent(ILocalModelProviderResolver providerResolver,
        IOptions<MemoryExtractionOptions> options,
        ILogger<DefaultMemoryExtractionAgent> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(providerResolver);
        _providerResolver = providerResolver;
    }

    public async Task<IReadOnlyList<ProposedMemory>> ProposeAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Disabled gate: with no node-local extraction model there is no model call and no candidate. The service
        // checks this too, but the agent owns the privacy-critical call, so it guards independently.
        if (string.IsNullOrWhiteSpace(_options.ExtractionModelName))
        {
            return [];
        }

        // Route the extraction model to the runtime that serves it, node-local only and never the cloud singleton.
        // THIS resolution IS the privacy invariant: conversation content only ever reaches a per-provider client.
        var provider = await _providerResolver.ResolveProviderForModelAsync(_options.ExtractionModelName, cancellationToken);
        var selection = new LocalModelSelection
        {
            ModelName = _options.ExtractionModelName,
            ProviderName = provider.ProviderName
        };

        // IChatClient is IDisposable — dispose the per-run node-local client.
        using var chatClient = provider.CreateChatClient(selection).WithProviderTelemetry();

        List<ChatMessage> messages =
        [
            new(ChatRole.System, BuildSystemPrompt(_options.MaxCandidates, run.Failed)),
            new(ChatRole.User, JsonSerializer.Serialize(ToPromptModel(run), SerializerOptions))
        ];

        var chatOptions = new ChatOptions
        {
            Temperature = 0f
        };

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

    private static object ToPromptModel(MemoryExtractionRunInput run)
    {
        // Hand the model only what it needs to distill a durable lesson: the user turns, the assistant's answer, and
        // whether the run failed (+ the sanitized error string for a failed run). No ids — the model never needs them.
        return new
        {
            UserTurns = run.UserTurns.Select(static turn => turn.Content).ToArray(),
            run.AssistantResponse,
            run.Failed,
            Error = run.Failed ? run.Error : null
        };
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
