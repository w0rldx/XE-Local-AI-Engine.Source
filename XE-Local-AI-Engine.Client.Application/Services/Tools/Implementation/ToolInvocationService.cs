namespace XE_Local_AI_Engine.Client.Services.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The one place a workflow node's tool call is admitted or refused. Every gate runs here — catalog match, risk
///     class, composed approval, structural approval floor, argument validation, budget — so a caller cannot skip one
///     by construction, and <see cref="ListInvocableToolsAsync" /> walks the same gates so a picker or a save-time
///     validator can never disagree with what the runtime will actually run.
///     <para>
///         <b>Not cached.</b> The catalog is re-read on every call, opening a scope for the custom-tool store each
///         time. Deliberate: these are operator-paced calls, and the composed approval read in gate 2 comes live from
///         node settings — a cache would go stale against a TIGHTENED policy, which is the one direction that matters.
///     </para>
///     <para>
///         <b>No audit row.</b> ADR 0006's strict pre-invocation record exists for adapting an approval-required
///         function into a non-approval one for an agentic MCP root; this service refuses that class twice and adapts
///         nothing, so an <c>approve</c> row would assert a decision nobody made.
///     </para>
/// </summary>
internal sealed class ToolInvocationService(
    ILocalToolOfferProvider offerProvider,
    IToolApprovalPolicy approvalPolicy,
    IAgentToolRegistry toolRegistry,
    IClientLocalToolRegistry clientLocalToolRegistry,
    ILogger<ToolInvocationService> logger) : IToolInvocationService
{
    /// <summary>
    ///     The catalog <c>Source</c> tag for an in-process built-in, matched ORDINALLY rather than taking the first
    ///     name match: the catalog appends custom-tool entries after the built-ins, so first-match-wins would resolve a
    ///     custom tool named <c>read_file</c> to the built-in only by accident of ordering.
    /// </summary>
    private const string BuiltinSource = "builtin";

    private static readonly JsonSerializerOptions ResultSerializerOptions = JsonSerializerOptions.Web;

    private readonly IToolApprovalPolicy _approvalPolicy = approvalPolicy ?? throw new ArgumentNullException(nameof(approvalPolicy));
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry = clientLocalToolRegistry ?? throw new ArgumentNullException(nameof(clientLocalToolRegistry));
    private readonly ILogger<ToolInvocationService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ILocalToolOfferProvider _offerProvider = offerProvider ?? throw new ArgumentNullException(nameof(offerProvider));
    private readonly IAgentToolRegistry _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));

    /// <inheritdoc />
    public async Task<ToolInvocationOutcome> InvokeAsync(string toolName,
        string argumentsJson,
        ToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Step 1a. Blank first, which is also what keeps the registry lookups below from throwing on an empty name.
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return new ToolInvocationOutcome(ToolInvocationOutcomeKind.UnknownTool, null, "The node names no tool.");
        }

        // The deadline is its OWN source rather than a CancelAfter on the linked budget, and the catch below is why:
        // classifying off the CALLER's token would report a terminal Cancelled for a retryable Timeout whenever that
        // token happens to fire between the budget expiring and the catch reading it.
        //
        // Armed HERE, before the catalog is even read: the caller's budget covers the WHOLE call, so a slow catalog
        // read spends it like anything else. Armed after the lookup, a node could wait its budget out on the catalog
        // and then hand the tool a second, full one. A budget already spent is cancelled synchronously, so an expired
        // deadline never depends on timer resolution.
        using var deadline = new CancellationTokenSource();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        if (context.Timeout <= TimeSpan.Zero)
        {
            await deadline.CancelAsync().ConfigureAwait(false);
        }
        else
        {
            deadline.CancelAfter(context.Timeout);
        }

        // Everything from here is inside one try: the contract is that no bad call throws, and a caller's token can
        // fire inside the catalog read just as easily as inside the tool.
        try
        {
            // Step 1b. The model-agnostic CATALOG, not the offer: a workflow node has no active model, so the offer's
            // capability gate would silently withhold six of the eight invocable tools.
            var catalog = await _offerProvider.GetKnownToolsAsync(budget.Token).ConfigureAwait(false);
            var entry = catalog.FirstOrDefault(candidate => string.Equals(candidate.Name, toolName, StringComparison.Ordinal)
                                                           && string.Equals(candidate.Source, BuiltinSource, StringComparison.Ordinal));
            if (entry is null)
            {
                return new ToolInvocationOutcome(ToolInvocationOutcomeKind.UnknownTool, null, $"'{toolName}' is not a built-in tool on this node.");
            }

            // Steps 2-5: risk class, composed approval, executable resolution, structural approval floor.
            if (TryAdmit(entry, out var executable) is { } refusal)
            {
                return new ToolInvocationOutcome(ToolInvocationOutcomeKind.NotInvocable, null, refusal);
            }

            // Step 6. Parse the arguments into a bag the validator and the function both read.
            if (!TryParseArguments(argumentsJson, out var arguments, out var parseError))
            {
                return new ToolInvocationOutcome(ToolInvocationOutcomeKind.InvalidArguments, null, parseError);
            }

            // Step 7. The same validator, schema and strictness the registry's own wrapper applies — run BEFORE the
            // call so a schema violation is the node's failure rather than a successful node output carrying repair
            // guidance meant for a model.
            var validation = ToolArgumentValidator.CoerceAndValidate(executable.JsonSchema, arguments!, rejectUnknownProperties: true);
            if (!validation.IsValid)
            {
                return new ToolInvocationOutcome(ToolInvocationOutcomeKind.InvalidArguments, null, validation.Reason ?? $"The arguments for '{entry.Name}' are invalid.");
            }

            // Step 8. Whatever is left of the budget armed above — the validation this call has already done came out
            // of the same one.
            budget.Token.ThrowIfCancellationRequested();
            var result = Stringify(await executable.InvokeAsync(arguments!, budget.Token).ConfigureAwait(false));

            // Step 10. A repair envelope means the wrapper's own deserialization branch answered rather than the tool.
            // Pre-validation cannot reach that branch, so the result is inspected before it counts as a success.
            if (TryReadRepairReason(result) is { } repairReason)
            {
                return new ToolInvocationOutcome(ToolInvocationOutcomeKind.InvalidArguments, null, repairReason);
            }

            return new ToolInvocationOutcome(ToolInvocationOutcomeKind.Executed, result, "read-local");
        }
        catch (OperationCanceledException)
        {
            // Step 11a. Whose deadline fired. The budget this service imposed is asked FIRST and by its own source:
            // a spent budget is a timeout however many other tokens have fired since, and the two answers are not
            // interchangeable — a timeout is re-attempted and a cancellation is not.
            return deadline.IsCancellationRequested
                ? new ToolInvocationOutcome(ToolInvocationOutcomeKind.Timeout, null, $"'{toolName}' exceeded the node's time budget.")
                : new ToolInvocationOutcome(ToolInvocationOutcomeKind.Cancelled, null, $"The invocation of '{toolName}' was cancelled.");
        }
        catch (Exception exception)
        {
            // Step 11b. The reason names the tool and nothing else: an exception message can carry a path or an
            // argument value, so it goes to a Debug log an operator surface never renders.
            _logger.LogDebug(exception,
                "Tool {ToolName} threw for node {NodeKey} (node run {NodeRunId}) of graph-workflow run {RunId}.",
                toolName,
                context.NodeKey,
                context.NodeRunId,
                context.RunId);
            return new ToolInvocationOutcome(ToolInvocationOutcomeKind.Faulted, null, $"'{toolName}' threw during invocation.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvocableToolDescriptor>> ListInvocableToolsAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _offerProvider.GetKnownToolsAsync(cancellationToken).ConfigureAwait(false);
        var invocable = new List<InvocableToolDescriptor>();
        foreach (var entry in catalog)
        {
            if (!string.Equals(entry.Source, BuiltinSource, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryAdmit(entry, out var executable) is not null)
            {
                continue;
            }

            invocable.Add(new InvocableToolDescriptor(entry.Name, entry.Description, executable.JsonSchema.GetRawText()));
        }

        return invocable;
    }

    /// <summary>
    ///     The envelope, applied to one catalog entry. Returns <see langword="null" /> when the tool may run (and hands
    ///     back its executable), or the structural refusal reason.
    /// </summary>
    private string? TryAdmit(LocalToolCatalogEntry entry, out AIFunction executable)
    {
        executable = null!;

        // Gate 1. Risk class. ReadLocal is the only class a node may run unattended (D6).
        if (entry.Category != ToolCategory.ReadLocal)
        {
            return "not-read-local";
        }

        // Gate 2. The composed effective approval, tighten-only over the catalog default. A node policy tightening
        // ReadLocal closes every Tool node — deliberate.
        if (_approvalPolicy.RequiresApproval(entry.Name, entry.Category, entry.RequiresApproval))
        {
            return "approval-gated";
        }

        // Step 4. BOTH registries: the built-in chat registry carries two tools, and the other six invocable ones are
        // worker-owned handlers. `as` rather than a cast, so a non-function tool is refused instead of throwing.
        var resolved = _toolRegistry.GetLocalChatTools()
                                    .FirstOrDefault(tool => string.Equals(tool.Name, entry.Name, StringComparison.Ordinal))
                       ?? (_clientLocalToolRegistry.TryResolve(entry.Name, out var clientLocal) ? clientLocal : null);
        if ((resolved as AIFunction) is not { } function)
        {
            return "no-executable";
        }

        // Step 5. The structural floor. Unreachable today given gate 2, kept because the registry PRE-WRAP — not the
        // policy — is the last line of defence and can change independently of it. Never unwrapped.
        if (function is ApprovalRequiredAIFunction)
        {
            return "approval-gated";
        }

        executable = function;
        return null;
    }

    // From HeadlessToolExecutor.TryParseArguments (minus the element it does not need): the arguments are cloned
    // property-by-property into an ordinal bag, which is what both the validator and AIFunction read. The parser's own
    // message is the one deliberate departure — see the catch.
    private static bool TryParseArguments(string argumentsJson, out AIFunctionArguments? arguments, out string error)
    {
        arguments = null;
        error = string.Empty;
        var text = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The tool arguments are not a JSON object.";
                return false;
            }

            var element = document.RootElement.Clone();
            var bag = new AIFunctionArguments(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                bag[property.Name] = property.Value;
            }

            arguments = bag;
            return true;
        }
        catch (JsonException)
        {
            // The parser's message names the offending characters and their position, which is argument CONTENT. A
            // reason is read off an operator surface, and this one promises to stay structural.
            error = "The tool arguments are not valid JSON.";
            return false;
        }
    }

    // Copied verbatim from HeadlessToolExecutor.Stringify. AIFunction.InvokeAsync hands back the serialized return
    // value, so a string-returning tool (every invocable one today) arrives as a JSON string element to unwrap.
    private static string Stringify(object? result) =>
        result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } text => text.GetString() ?? string.Empty,
            JsonElement json => json.GetRawText(),
            _ => JsonSerializer.Serialize(result, ResultSerializerOptions)
        };

    /// <summary>
    ///     The <c>reason</c> of a <c>ToolArgumentRepairResult</c> envelope, or <see langword="null" /> when the result
    ///     is a real tool answer. <c>tool_disabled</c> cannot fire outside a repair scope and costs nothing to cover.
    /// </summary>
    private static string? TryReadRepairReason(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var code = error.GetString();
            if (code is not ("invalid_arguments" or "tool_disabled"))
            {
                return null;
            }

            return document.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString() ?? code
                : code;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
