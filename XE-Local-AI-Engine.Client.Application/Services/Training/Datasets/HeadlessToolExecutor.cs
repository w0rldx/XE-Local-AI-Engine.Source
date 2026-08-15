namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

public enum HeadlessToolOutcomeKind
{
    /// <summary>The real tool ran in-process — only ever for a ReadLocal tool whose composed approval is false.</summary>
    Executed,

    /// <summary>A statically verified mock answered.</summary>
    Mocked,

    /// <summary>Nothing ran: the call was validated but no real execution was permitted and no mock matched.</summary>
    ValidationOnly,

    /// <summary>The call could not be honored at all (unknown tool, unusable arguments, a throwing tool).</summary>
    Failed
}

public sealed record HeadlessToolOutcome(HeadlessToolOutcomeKind Kind, string? Result, string Reason);

public interface IHeadlessToolExecutor
{
    /// <summary>
    ///     Executes one generated tool call under the training approval gate. Real execution requires BOTH
    ///     <see cref="ToolCategory.ReadLocal" /> AND a composed effective approval of <see langword="false" />; anything
    ///     else routes to the mock engine or returns a clearly-marked validation-only outcome. Never throws for a bad
    ///     call — a failure is a per-sample outcome, not a crash.
    /// </summary>
    Task<HeadlessToolOutcome> ExecuteAsync(string toolName, string argumentsJson, string? teacherModelName, CancellationToken cancellationToken = default);
}

/// <summary>
///     The policy-aware execution seam for dataset generation (plan invariant #4). It deliberately does NOT go through
///     <c>InvocationRunner.ExecuteApiToolCallAsync</c>: that overload is a hub/worker round-trip (it registers a pending
///     tool call, sends a payload over SignalR and awaits a remote result) and executes nothing in-process. This resolves
///     the executable <see cref="AIFunction" /> from the registry and invokes it directly, after its own
///     <see cref="IToolApprovalPolicy.RequiresApproval" /> call — that call is the tested enforcement point.
///     <para>
///         follow-up: generation deliberately does not emit <c>IToolApprovalAuditRecorder</c> records in v1. Every layer
///         outcome (including this one) is persisted per sample in <c>ValidationJson</c>, which is the audit surface for
///         generation; wiring the interactive-chat recorder as well is a later consistency choice, not a gap.
///     </para>
/// </summary>
internal sealed class HeadlessToolExecutor(
    ILocalToolOfferProvider offerProvider,
    IToolApprovalPolicy approvalPolicy,
    IAgentToolRegistry toolRegistry,
    ITrainingDatasetStore store,
    IToolMockEngine mockEngine,
    IToolMockStaticVerifier mockVerifier,
    ILogger<HeadlessToolExecutor> logger) : IHeadlessToolExecutor
{
    private readonly IToolApprovalPolicy _approvalPolicy = approvalPolicy ?? throw new ArgumentNullException(nameof(approvalPolicy));
    private readonly ILogger<HeadlessToolExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IToolMockEngine _mockEngine = mockEngine ?? throw new ArgumentNullException(nameof(mockEngine));
    private readonly IToolMockStaticVerifier _mockVerifier = mockVerifier ?? throw new ArgumentNullException(nameof(mockVerifier));
    private readonly ILocalToolOfferProvider _offerProvider = offerProvider ?? throw new ArgumentNullException(nameof(offerProvider));
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAgentToolRegistry _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));

    public async Task<HeadlessToolOutcome> ExecuteAsync(string toolName,
        string argumentsJson,
        string? teacherModelName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, "The generated call names no tool.");
        }

        var offered = await _offerProvider.GetOfferedToolsAsync(teacherModelName, isCloudModel: false, cancellationToken).ConfigureAwait(false);
        var offer = offered.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        if (offer is null)
        {
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, $"The tool catalog does not offer '{toolName}'.");
        }

        if (!TryParseArguments(argumentsJson, out var arguments, out var argumentsElement, out var parseError))
        {
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, parseError);
        }

        // THE enforcement point: the composed effective approval for this tool, tighten-only over the catalog default.
        var requiresApproval = _approvalPolicy.RequiresApproval(offer.Name, offer.Category, offer.RequiresApproval);
        if (offer.Category == ToolCategory.ReadLocal && !requiresApproval)
        {
            return await ExecuteRealAsync(offer, arguments!, cancellationToken).ConfigureAwait(false);
        }

        return await RespondFromMockAsync(offer.Name, argumentsElement, requiresApproval, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HeadlessToolOutcome> ExecuteRealAsync(AllowedToolDto offer, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var executable = _toolRegistry.GetLocalChatTools()
                                      .OfType<AIFunction>()
                                      .FirstOrDefault(tool => string.Equals(tool.Name, offer.Name, StringComparison.Ordinal));
        if (executable is null)
        {
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, $"'{offer.Name}' has no executable in the local tool registry.");
        }

        if (executable is ApprovalRequiredAIFunction)
        {
            // The registry pre-wrap is the structural floor. A wrapped executable can only run behind a human approval
            // round-trip, which headless generation has no route to — so it is mocked, never unwrapped.
            return await RespondFromMockAsync(offer.Name, ParseElement(arguments), requiresApproval: true, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(offer.ParameterSchema))
        {
            try
            {
                using var schema = JsonDocument.Parse(offer.ParameterSchema);
                var validation = ToolArgumentValidator.CoerceAndValidate(schema.RootElement, arguments);
                if (!validation.IsValid)
                {
                    return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, validation.Reason ?? "The generated arguments are invalid.");
                }
            }
            catch (JsonException exception)
            {
                return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, $"The tool's parameter schema is unreadable: {exception.Message}");
            }
        }

        try
        {
            var result = await executable.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Executed, Stringify(result), "read-local");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A throwing tool is one sample's failure, never the generation run's.
            _logger.LogDebug(exception, "Headless execution of {ToolName} failed during dataset generation.", offer.Name);
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, $"'{offer.Name}' threw during headless execution.");
        }
    }

    private async Task<HeadlessToolOutcome> RespondFromMockAsync(string toolName,
        JsonElement arguments,
        bool requiresApproval,
        CancellationToken cancellationToken)
    {
        var reason = requiresApproval ? "approval-gated" : "not-read-local";
        var mocks = await _store.ListUsableMocksAsync(toolName, cancellationToken).ConfigureAwait(false);
        foreach (var mock in mocks)
        {
            if (!_mockVerifier.TryParse(mock.MockJson.Span, out var body, out _) || body is null)
            {
                continue;
            }

            if (_mockEngine.TryRespond(body, arguments) is { } response)
            {
                return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Mocked, response, reason);
            }
        }

        return new HeadlessToolOutcome(HeadlessToolOutcomeKind.ValidationOnly, null,
            mocks.Count == 0
                ? $"{reason}; no verified, enabled mock exists for '{toolName}'."
                : $"{reason}; no mock rule matched the generated arguments for '{toolName}'.");
    }

    private static bool TryParseArguments(string argumentsJson,
        out AIFunctionArguments? arguments,
        out JsonElement element,
        out string error)
    {
        arguments = null;
        element = default;
        error = string.Empty;
        var text = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The generated tool arguments are not a JSON object.";
                return false;
            }

            element = document.RootElement.Clone();
            var bag = new AIFunctionArguments(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                bag[property.Name] = property.Value;
            }

            arguments = bag;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The generated tool arguments are not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static JsonElement ParseElement(AIFunctionArguments arguments) =>
        JsonSerializer.SerializeToElement(arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), TrainingJson.Options);

    private static string Stringify(object? result) =>
        result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement json => json.GetRawText(),
            _ => JsonSerializer.Serialize(result, TrainingJson.Options)
        };
}
