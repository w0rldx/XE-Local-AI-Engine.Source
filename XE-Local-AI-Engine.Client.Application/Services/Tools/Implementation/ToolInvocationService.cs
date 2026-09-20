namespace XE_Local_AI_Engine.Client.Services.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>The one place a workflow node's tool call is admitted or refused.</summary>
/// <remarks>
///     Every gate runs here — catalog match, risk class, composed approval, structural approval floor, argument validation, budget — so a caller
///     cannot skip one by construction, and <see cref="ListInvocableToolsAsync" /> walks the same gates, so a picker or a save-time validator can
///     never disagree with what the runtime will actually run. Why the catalog is re-read on every call, and why no approval-audit row is written:
///     docs/wiki/12-security-and-privacy.md ("Tool invocation writes no approval-audit row").
/// </remarks>
/// <seealso cref="IToolInvocationService" />
internal sealed class ToolInvocationService : IToolInvocationService
{
    /// <summary>The catalog <c>Source</c> tag for an in-process built-in, matched ORDINALLY rather than by first name match.</summary>
    /// <remarks>
    ///     The catalog appends custom-tool entries after the built-ins, so first-match-wins would resolve a custom tool
    ///     named <c>read_file</c> to the built-in only by accident of ordering.
    /// </remarks>
    private const string BuiltinSource = "builtin";

    private static readonly JsonSerializerOptions ResultSerializerOptions = JsonSerializerOptions.Web;

    private readonly IToolApprovalPolicy _approvalPolicy;
    private readonly IClientLocalToolRegistry _clientLocalToolRegistry;
    private readonly ILogger<ToolInvocationService> _logger;
    private readonly ILocalToolOfferProvider _offerProvider;
    private readonly IAgentToolRegistry _toolRegistry;

    public ToolInvocationService(
        ILocalToolOfferProvider offerProvider,
        IToolApprovalPolicy approvalPolicy,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        ILogger<ToolInvocationService> logger)
    {
        ArgumentNullException.ThrowIfNull(approvalPolicy);
        ArgumentNullException.ThrowIfNull(clientLocalToolRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(offerProvider);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        _approvalPolicy = approvalPolicy;
        _clientLocalToolRegistry = clientLocalToolRegistry;
        _logger = logger;
        _offerProvider = offerProvider;
        _toolRegistry = toolRegistry;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The deadline is its OWN cancellation source, not a <c>CancelAfter</c> on the linked budget: classifying off the CALLER's token would report a
    ///     terminal <c>Cancelled</c> for a retryable <c>Timeout</c> whenever that token fired between the budget expiring and the catch reading it. It is
    ///     armed BEFORE the catalog is read, so a slow read spends the caller's budget rather than leaving the tool a second, full one, and a spent budget
    ///     cancels synchronously. The arming sits inside the try because <c>CancelAfter</c> refuses a span past <c>int.MaxValue</c> ms and a node may
    ///     declare one.
    /// </remarks>
    public async Task<ToolInvocationOutcome> InvokeAsync(string toolName,
        string argumentsJson,
        ToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Step 1a. Blank first, which is also what keeps the registry lookups below from throwing on an empty name.
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.UnknownTool, Result = null, Reason = "The node names no tool." };
        }

        // Only the DECLARATION is out here, where the catch can still read it; the arming is inside the try. See this method's remarks.
        using var deadline = new CancellationTokenSource();

        // Everything from here is inside one try: the contract is that no bad call throws, and a caller's token can
        // fire inside the catalog read just as easily as inside the tool.
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            if (context.Timeout <= TimeSpan.Zero)
            {
                await deadline.CancelAsync();
            }
            else
            {
                deadline.CancelAfter(context.Timeout);
            }

            // Step 1b. The model-agnostic CATALOG, not the offer: a workflow node has no active model, so the offer's
            // capability gate would silently withhold six of the eight invocable tools.
            var catalog = await _offerProvider.GetKnownToolsAsync(budget.Token);
            var entry = catalog.FirstOrDefault(candidate => string.Equals(candidate.Name, toolName, StringComparison.Ordinal)
                                                            && string.Equals(candidate.Source, BuiltinSource, StringComparison.Ordinal));
            if (entry is null)
            {
                return new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.UnknownTool, Result = null, Reason = $"'{toolName}' is not a built-in tool on this node." };
            }

            // Steps 2-5: risk class, composed approval, executable resolution, structural approval floor.
            if (TryAdmit(entry, out var executable) is { } refusal)
            {
                return new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.NotInvocable, Result = null, Reason = refusal };
            }

            // Step 6. Parse the arguments into a bag the validator and the function both read.
            if (!TryParseArguments(argumentsJson, out var arguments, out var parseError))
            {
                return new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.InvalidArguments, Result = null, Reason = parseError };
            }

            // Step 7. The same validator, schema and strictness the registry's own wrapper applies — run BEFORE the call so a schema
            // violation is the node's failure rather than a successful node output carrying repair guidance meant for a model.
            var validation = ToolArgumentValidator.CoerceAndValidate(executable.JsonSchema, arguments!, rejectUnknownProperties: true);
            if (!validation.IsValid)
            {
                return new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.InvalidArguments, Result = null, Reason = validation.Reason ?? $"The arguments for '{entry.Name}' are invalid." };
            }

            // Step 8. Whatever is left of the budget armed above — the validation this call has already done came out
            // of the same one.
            budget.Token.ThrowIfCancellationRequested();
            var result = Stringify(await executable.InvokeAsync(arguments!, budget.Token));

            // Step 10. A repair envelope means the wrapper's own deserialization branch answered rather than the tool.
            // Pre-validation cannot reach that branch, so the result is inspected before it counts as a success.
            if (TryReadRepairReason(result) is { } repairReason)
            {
                return new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.InvalidArguments, Result = null, Reason = repairReason };
            }

            return new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.Executed, Result = result, Reason = "read-local" };
        }
        catch (OperationCanceledException)
        {
            // Step 11a. Whose deadline fired. The budget this service imposed is asked FIRST and by its own source: a spent budget is a timeout however
            // many other tokens have fired since, and the two answers are not interchangeable — a timeout is re-attempted and a cancellation is not.
            return deadline.IsCancellationRequested
                ? new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.Timeout, Result = null, Reason = $"'{toolName}' exceeded the node's time budget." }
                : new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.Cancelled, Result = null, Reason = $"The invocation of '{toolName}' was cancelled." };
        }
        catch (Exception exception)
        {
            // Step 11b. The reason names the tool and nothing else: an exception message can carry a path or an argument value, so it goes to a Debug log no operator surface renders.
            // Not every caller is a graph-workflow node — the dataset generator invokes with no run of its own and identifies itself in NodeKey alone, so a run of all zeroes would read as real.
            if (context.RunId == Guid.Empty)
            {
                _logger.LogDebug(exception, "Tool {ToolName} threw for {NodeKey}.", toolName, context.NodeKey);
            }
            else
            {
                _logger.LogDebug(exception,
                    "Tool {ToolName} threw for node {NodeKey} (node run {NodeRunId}) of graph-workflow run {RunId}.",
                    toolName,
                    context.NodeKey,
                    context.NodeRunId,
                    context.RunId);
            }

            return new ToolInvocationOutcome { Kind = ToolInvocationOutcomeKind.Faulted, Result = null, Reason = $"'{toolName}' threw during invocation." };
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InvocableToolDescriptor>> ListInvocableToolsAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _offerProvider.GetKnownToolsAsync(cancellationToken);
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

            invocable.Add(new InvocableToolDescriptor { Name = entry.Name, Description = entry.Description, ParameterSchema = executable.JsonSchema.GetRawText() });
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

        // Gate 1. Risk class. ReadLocal is the only class a node may run unattended.
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

    // From HeadlessToolExecutor.TryParseArguments (minus the element it does not need): the arguments are cloned property-by-property into an
    // ordinal bag, which is what both the validator and AIFunction read. The parser's own message is the one deliberate departure — see the catch.
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
