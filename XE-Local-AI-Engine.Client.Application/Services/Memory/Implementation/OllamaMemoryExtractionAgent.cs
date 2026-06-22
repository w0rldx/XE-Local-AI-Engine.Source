namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Default <see cref="IMemoryExtractionAgent" />: runs a <b>node-local</b> model (resolved per-model via
///     <see cref="ILocalModelProviderResolver" />, never the shared <see cref="IChatClient" /> singleton which can be a
///     cloud client) and forces a structured JSON response so each candidate carries its scope + confidence. The run's
///     user turns and assistant answer are read into the model on-node only — they never cross the node boundary.
///     This type is intentionally not unit-tested against a live model; tests substitute a fake
///     <see cref="IMemoryExtractionAgent" /> (mirroring the analysis agent seam, so no Ollama is needed in CI).
/// </summary>
internal sealed class OllamaMemoryExtractionAgent(
    ILocalModelProviderResolver providerResolver,
    IOptions<MemoryExtractionOptions> options,
    ILogger<OllamaMemoryExtractionAgent> logger) : IMemoryExtractionAgent
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<OllamaMemoryExtractionAgent> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MemoryExtractionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));

    public async Task<IReadOnlyList<ProposedMemory>> ProposeAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Disabled gate: no node-local extraction model configured => no model call, no candidate. Mirrors the
        // embedding-ranker disabled gate so CI stays deterministic without Ollama. (The service also checks this, but
        // the agent owns the privacy-critical model call, so it guards independently.)
        if (string.IsNullOrWhiteSpace(_options.ExtractionModelName))
        {
            return [];
        }

        // Route the configured extraction model to the runtime that serves it (persisted map, else the configured
        // default provider = ollama, so an un-repointed model behaves exactly as the analysis path). Node-local only —
        // never the cloud singleton. THIS resolution is the privacy invariant: conversation content only ever reaches a
        // provider.CreateChatClient(...) client, never the shared cloud-capable IChatClient.
        var provider = await _providerResolver.ResolveProviderForModelAsync(_options.ExtractionModelName, cancellationToken).ConfigureAwait(false);
        var selection = new LocalModelSelection
        {
            ModelName = _options.ExtractionModelName,
            ProviderName = provider.ProviderName
        };

        // IChatClient is IDisposable — dispose the per-run node-local client.
        using var chatClient = provider.CreateChatClient(selection);

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
                             .GetResponseAsync<ExtractionEnvelope>(messages, chatOptions, cancellationToken: cancellationToken)
                             .ConfigureAwait(false);

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
        return new ProposedMemory(candidate.Behavior ?? string.Empty,
            MapScope(candidate.Scope),
            candidate.TriggerCondition,
            candidate.Confidence);
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
            AssistantResponse = run.AssistantResponse,
            Failed = run.Failed,
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
