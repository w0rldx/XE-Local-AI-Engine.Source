namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Tools;

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
///     The policy-aware execution seam for dataset generation. It deliberately does NOT go through
///     <c>ApiToolCallBridge.ExecuteApiToolCallAsync</c>: that overload is a hub/worker round-trip (it registers a pending
///     tool call, sends a payload over SignalR and awaits a remote result) and executes nothing in-process.
///     <para>
///         This class owns the two things generation adds on top of a plain in-process call — the teacher model's OFFER
///         as the catalog (so a capability-gated or custom tool resolves exactly as the teacher saw it) and the MOCK
///         envelope for everything real execution is not permitted to run. Real execution itself is delegated whole to
///         <see cref="IToolInvocationService.InvokeAsync" />, which owns executable resolution across BOTH registries,
///         the structural approval floor, argument validation, the call and its classification. Its own
///         <see cref="IToolApprovalPolicy.RequiresApproval" /> call below stays the tested enforcement point for the
///         mock/real routing decision; the shared seam re-composes the same approval because its contract is that no
///         caller can skip a gate, and the read is an idempotent settings lookup.
///     </para>
///     <para>
///         Generation deliberately does not emit <c>IToolApprovalAuditRecorder</c> records in v1. Every layer
///         outcome (including this one) is persisted per sample in <c>ValidationJson</c>, which is the audit surface for
///         generation; the interactive-chat recorder is not part of this audit path.
///     </para>
/// </summary>
internal sealed class HeadlessToolExecutor(
    ILocalToolOfferProvider offerProvider,
    IToolApprovalPolicy approvalPolicy,
    ITrainingDatasetStore store,
    IToolMockEngine mockEngine,
    IToolMockStaticVerifier mockVerifier,
    IToolInvocationService toolInvocation) : IHeadlessToolExecutor
{
    /// <summary>
    ///     The node key the shared seam logs this caller under. Generation has no run or node of its own, so the two
    ///     ids it carries are <see cref="Guid.Empty" /> and this string is what identifies the call in a Debug log.
    /// </summary>
    private const string GenerationNodeKey = "training:dataset-generation";

    /// <summary>
    ///     Effectively unbounded, and deliberately so: this class imposed no budget before the shared seam existed, and
    ///     a generation run is already cancellable through the token its caller threads in. It is a finite span rather
    ///     than <see cref="Timeout.InfiniteTimeSpan" /> because the seam treats a non-positive budget as already spent,
    ///     and <c>CancelAfter</c> refuses anything past <see cref="int.MaxValue" /> milliseconds.
    /// </summary>
    private static readonly TimeSpan UnboundedBudget = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly IToolApprovalPolicy _approvalPolicy = approvalPolicy ?? throw new ArgumentNullException(nameof(approvalPolicy));
    private readonly IToolMockEngine _mockEngine = mockEngine ?? throw new ArgumentNullException(nameof(mockEngine));
    private readonly IToolMockStaticVerifier _mockVerifier = mockVerifier ?? throw new ArgumentNullException(nameof(mockVerifier));
    private readonly ILocalToolOfferProvider _offerProvider = offerProvider ?? throw new ArgumentNullException(nameof(offerProvider));
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IToolInvocationService _toolInvocation = toolInvocation ?? throw new ArgumentNullException(nameof(toolInvocation));

    public async Task<HeadlessToolOutcome> ExecuteAsync(string toolName,
        string argumentsJson,
        string? teacherModelName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, "The generated call names no tool.");
        }

        var offered = await _offerProvider.GetOfferedToolsAsync(teacherModelName, isCloudModel: false, cancellationToken);
        var offer = offered.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        if (offer is null)
        {
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, $"The tool catalog does not offer '{toolName}'.");
        }

        if (!TryParseArguments(argumentsJson, out var argumentsElement, out var parseError))
        {
            return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, parseError);
        }

        // THE enforcement point: the composed effective approval for this tool, tighten-only over the catalog default.
        var requiresApproval = _approvalPolicy.RequiresApproval(offer.Name, offer.Category, offer.RequiresApproval);
        if (offer.Category == ToolCategory.ReadLocal && !requiresApproval)
        {
            return await ExecuteRealAsync(offer.Name, argumentsJson, argumentsElement, cancellationToken);
        }

        return await RespondFromMockAsync(offer.Name, argumentsElement, requiresApproval, cancellationToken);
    }

    /// <summary>
    ///     The real call, delegated whole to the shared invocation seam. Its refusals map back onto this class's own
    ///     vocabulary: a structural approval refusal (the registry pre-wrap, or a policy that tightened between this
    ///     class's compose and the seam's) is MOCKED, never unwrapped, because headless generation has no route to a
    ///     human approval round-trip; a risk-class refusal is mocked for the same reason the caller's gate would have
    ///     mocked it. Everything else — an unresolvable tool, invalid arguments, a spent budget, a throwing tool — is
    ///     one sample's failure. A cancellation is the generation RUN's, so it is rethrown rather than recorded.
    /// </summary>
    private async Task<HeadlessToolOutcome> ExecuteRealAsync(string toolName,
        string argumentsJson,
        JsonElement argumentsElement,
        CancellationToken cancellationToken)
    {
        var outcome = await _toolInvocation.InvokeAsync(toolName,
                                               argumentsJson,
                                               new ToolInvocationContext(Guid.Empty, Guid.Empty, GenerationNodeKey, UnboundedBudget),
                                               cancellationToken);

        switch (outcome.Kind)
        {
            case ToolInvocationOutcomeKind.Executed:
                return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Executed, outcome.Result, "read-local");
            case ToolInvocationOutcomeKind.NotInvocable when string.Equals(outcome.Reason, "approval-gated", StringComparison.Ordinal):
                return await RespondFromMockAsync(toolName, argumentsElement, requiresApproval: true, cancellationToken);
            case ToolInvocationOutcomeKind.NotInvocable when string.Equals(outcome.Reason, "not-read-local", StringComparison.Ordinal):
                return await RespondFromMockAsync(toolName, argumentsElement, requiresApproval: false, cancellationToken);
            // The seam answers this one with a structural token rather than a sentence, and a per-sample reason is
            // written into ValidationJson for a human to read. The other refusals already carry prose.
            case ToolInvocationOutcomeKind.NotInvocable when string.Equals(outcome.Reason, "no-executable", StringComparison.Ordinal):
                return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, $"'{toolName}' has no executable in the local tool registry.");
            case ToolInvocationOutcomeKind.Cancelled:
                cancellationToken.ThrowIfCancellationRequested();
                return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, outcome.Reason);
            default:
                return new HeadlessToolOutcome(HeadlessToolOutcomeKind.Failed, null, outcome.Reason);
        }
    }

    private async Task<HeadlessToolOutcome> RespondFromMockAsync(string toolName,
        JsonElement arguments,
        bool requiresApproval,
        CancellationToken cancellationToken)
    {
        var reason = requiresApproval ? "approval-gated" : "not-read-local";
        var mocks = await _store.ListUsableMocksAsync(toolName, cancellationToken);
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

    /// <summary>
    ///     The generated arguments as one JSON object element, which is what the MOCK engine matches its rules against.
    ///     The real path hands the raw text straight to the shared seam, which parses it into the bag the validator and
    ///     the function both read — so this parse exists only for the mock, and its message may still carry the parser's
    ///     detail because a per-sample reason is written into <c>ValidationJson</c>, not onto an operator surface.
    /// </summary>
    private static bool TryParseArguments(string argumentsJson, out JsonElement element, out string error)
    {
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
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The generated tool arguments are not valid JSON: {exception.Message}";
            return false;
        }
    }
}
