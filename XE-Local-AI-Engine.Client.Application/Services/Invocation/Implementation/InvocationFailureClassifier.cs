namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.ClientModel;
using System.Net;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Maps a terminal invocation exception to the pair the client is allowed to see: the category the UI branches on,
///     and a user-facing message.
/// </summary>
/// <remarks>
///     Pure functions with no invocation state, so the switch-arm ORDER in <see cref="MapFailure" /> is the whole contract
///     and <c>InvocationRunnerTests</c> pins it. An arm whose type derives from a later arm's type comes first; a new
///     <see cref="FailureCategory" /> drifts the generated OpenAPI/zod client, so prefer the closest existing one; only a
///     sanitized message is surfaced, a provider body only through <see cref="ExternalProviderErrorDetail" />.
/// </remarks>
internal static class InvocationFailureClassifier
{
    private const string AgentToolCallFailureMessage = "Worker tool execution failed.";

    // A tool call that ran out of time waiting for its RESULT (TurnPolicy.ToolResultTimeout: the package's ToolCallTimeoutSeconds or the node-global
    // pending-tool-call age). Separate from AgentToolCallFailureMessage so a tool-side timeout is attributable; it carries no tool name and stays path-free.
    private const string ToolCallTimedOutMessage = "A tool call timed out waiting for its result.";
    private const string ModelUnavailableMessage = "Selected model is not installed on this node.";
    private const string ProviderUnavailableMessage = "Provider unreachable.";

    private const string ResponseEndedMessage =
        "The model server stopped before the answer was finished; it may have exited. Send the message again: a local model is reloaded automatically.";

    // Surfaced when an endpoint's circuit breaker is open (recent consecutive transient failures): a fixed, path-free
    // message that tells the operator to retry shortly rather than reporting a hard provider outage.
    private const string ProviderTemporarilyUnavailableMessage = "Provider temporarily unavailable. Please retry shortly.";
    private const string ModelDoesNotSupportThinkingMessage = "This model does not support reasoning.";

    private const string ModelDoesNotSupportToolsMessage = "This model does not support tool calling.";

    // llama-server failed to COMPILE the constrained-decoding grammar for the offered tool schemas ("failed to parse grammar"). The model IS tool-capable
    // — the schema set is what could not be prepared — so this must never claim otherwise. Fixed and path-free: the provider body is never forwarded.
    private const string ToolCallingPreparationFailedMessage =
        "The model could not be prepared for tool calling with the current tool set. Retry with tools turned off, or select a different model.";

    // llama-server could not render the request through the model's own Jinja chat template (an HTTP 400 carrying "Jinja Exception" or "Unable to
    // generate parser for this template"). The raw template text is long and meaningless to an operator, so a fixed, path-free reason replaces it.
    private const string ChatTemplateRejectedMessage =
        "The model's chat template could not render this conversation. Select a different model, or retry without the agent's skills or tools.";

    // A provider HTTP 500 means the model was reached but failed to load OR run (an Ollama build too old for the architecture, an out-of-memory at load).
    // Phrased to cover both, so it never falsely asserts a permanent model defect while staying far more actionable than the generic "Provider unreachable.".
    private const string ModelLoadFailedMessage = InvocationFailureMessages.ModelLoadFailed;

    // A "Local runtime default" send found no installed GGUF chat model to route to. Surfaced instead of the generic
    // "Provider unreachable." so the operator gets an actionable next step (pull a GGUF model) rather than a dead-end.
    private const string NoChatModelInstalledMessage = "No chat model installed. Pull a GGUF model to start chatting.";

    // A generic (non-inter-chunk) timeout: the invocation-level cancel-after or an HTTP client timeout. Its framework
    // message can name hosts/paths and is unbounded, so a fixed, path-free constant is surfaced in its place.
    private const string TimedOutMessage = InvocationFailureMessages.TimedOut;

    // A provider or gateway HTTP 404: nothing is installed locally, so the node's "not installed" text would mislead.
    private const string ProviderModelNotOfferedMessage = "The provider does not offer the selected model. Check the model id on the connection.";

    // A provider or gateway HTTP 401/403. Fixed and path-free: only the sanitized body detail is appended, never the URL or credential.
    private const string ProviderAuthFailedMessage =
        "The provider rejected the credentials. Replace the API key, bearer token or secret headers in the connection settings.";

    private const string ProviderRateLimitedMessage = "The provider rate-limited this request";

    // Longer Retry-After values are dropped rather than cut: an HTTP-date is 29 characters, delta-seconds far fewer.
    private const int MaxRetryAfterLength = 64;

    // A 5xx reason phrase longer than this is dropped rather than cut.
    private const int MaxReasonPhraseLength = 64;

    private static readonly Regex ModelWordPattern = new(@"\bmodel\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly string[] ModelRefusalPhrases =
        ["not assigned", "not allowed", "not available", "not enabled", "not permitted", "no access", "does not exist", "not found", "unknown"];

    private static readonly Regex FrameworkExceptionNamePattern =
        new(@"\b(?:Microsoft|System)(?:\.[A-Za-z_][A-Za-z0-9_]*)*\.[A-Za-z_][A-Za-z0-9_]*Exception\b|\b(?:AgentException|ChatClientAgentException)\b", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    internal static FailureClassification MapFailure(Exception exception)
    {
        return exception switch
        {
            // Admission policies supply sanitized, user-facing refusal text. Surface it verbatim as an ordinary
            // terminal agent-runtime failure; the policy runs after warm-up but before any generation method is called.
            InvocationGenerationRejectedException generationRejected => new FailureClassification(FailureCategory.AgentRuntime, generationRejected.Message),
            // A capacity refusal of the model's launch reaches the chat path unwrapped. Its sanitized reason names only models and says what to do
            // ("Eject one of them or pick a loaded model"), so it is surfaced verbatim under its own category rather than as a model-load failure.
            LlamaCapacityRefusedException capacityRefused => new FailureClassification(FailureCategory.InsufficientCapacity, capacityRefused.Message),
            // No runtime installed and the Offline profile refused to fetch one: its sanitized reason says so and names Node Settings.
            _ when FindInChain<LlamaRuntimeNotInstalledException>(exception) is { } runtimeNotInstalled =>
                new FailureClassification(FailureCategory.ModelLoadFailed, runtimeNotInstalled.Message),
            // Matches BEFORE the generic InvalidOperationException arms below (both derive from InvalidOperationException):
            // a local-default send with no installed GGUF chat model surfaces ModelNotInstalled, not ProviderUnreachable.
            NoChatModelInstalledException => new FailureClassification(FailureCategory.ModelNotInstalled, NoChatModelInstalledMessage),
            // An operator force-ejected the model out from under an in-flight turn: an operator action, not a provider outage, so Cancelled keeps it out of
            // the failure count and its already user-safe message truthful. The OUTER type matches first, ahead of the transport arms its inner would hit.
            LlamaServerModelEjectedException modelEjected => new FailureClassification(FailureCategory.Cancelled, modelEjected.Message),
            // The budgeter's hard-stop — history still over budget after truncation — is a clean, classified pre-inference failure whose own
            // message IS the fixed, path-free ContextBudgetExceededMessage, so it is surfaced verbatim.
            ContextBudgetExceededException contextBudgetExceeded => new FailureClassification(FailureCategory.ContextWindowExceeded, contextBudgetExceeded.Message),
            // The call budget: a work session step's cap or the cumulative ceiling on a runaway loop. A bound, not a context overflow; its fixed,
            // path-free message is kept, and it matches before the generic InvalidOperationException arms it derives from.
            ProviderCallBudgetExceededException providerCallBudgetExceeded => new FailureClassification(FailureCategory.CallBudgetReached, providerCallBudgetExceeded.Message),
            // A single irreducible provider round — its pinned set alone exceeds the context window — failed by the per-round boundary before the provider is
            // called. Reuses ContextWindowExceeded with its own message; the bounded token/window diagnostics it holds are logged server-side, never surfaced.
            ProviderContextWindowExceededException providerContextWindowExceeded => new FailureClassification(FailureCategory.ContextWindowExceeded, providerContextWindowExceeded.Message),
            // An approval required in an unattended run that cannot obtain one. Its message is our own fixed-shape reason carrying nothing but a tool name,
            // and that reason IS the value of this failure over a generic approval timeout, so it is surfaced verbatim under AgentRuntime.
            ApprovalUnavailableException approvalUnavailable => new FailureClassification(FailureCategory.AgentRuntime, approvalUnavailable.Message),
            // A tripped circuit breaker: a fixed retry-soon message rather than the generic ProviderUnreachable, since the endpoint is likely
            // recovering rather than permanently down. It is not a TimeoutException, so it matches before the StreamIdle and Timeout arms.
            ProviderCircuitOpenException => new FailureClassification(FailureCategory.ProviderUnreachable, ProviderTemporarilyUnavailableMessage),
            // The inter-chunk stall carries the watchdog's own fixed, path-free message naming which timeout fired, so it is surfaced
            // verbatim. Matches BEFORE the generic TimeoutException arm it derives from.
            StreamIdleTimeoutException streamIdleTimeout => new FailureClassification(FailureCategory.Timeout, streamIdleTimeout.Message),
            // An unanswered tool approval names the tool it waited on; its message is our own fixed shape, so it is surfaced verbatim.
            ApprovalExpiredException approvalExpired => new FailureClassification(FailureCategory.Timeout, approvalExpired.Message),
            // Any other TimeoutException — the invocation-level cancel-after, or an HTTP client timeout surfacing as a bare one — carries an
            // unbounded framework message that can name hosts, paths and internals, so it collapses to a fixed, path-free constant.
            TimeoutException => new FailureClassification(FailureCategory.Timeout, TimedOutMessage),
            // A tool call the runner itself timed out (ToolResultTimeout or the pending-tool-call cleanup) wraps the timeout as its inner exception. Matched
            // BEFORE the generic tool-call arm, so a tool-side timeout is attributable instead of collapsing into the message every tool error uses.
            WorkerToolCallException { InnerException: TimeoutException or OperationCanceledException } =>
                new FailureClassification(FailureCategory.AgentToolCall, ToolCallTimedOutMessage),
            WorkerToolCallException => new FailureClassification(FailureCategory.AgentToolCall, AgentToolCallFailureMessage),
            NotSupportedException notSupportedException => new FailureClassification(FailureCategory.AgentRuntime, RedactAgentRuntimeMessage(notSupportedException.Message)),
            InvalidOperationException invalidOperationException when invalidOperationException.Message.Contains("Response size exceeded", StringComparison.Ordinal) =>
                new FailureClassification(FailureCategory.Unexpected, invalidOperationException.Message),
            // The Azure translator already sanitized its auth text (mode-aware). Other Azure kinds keep their old landing, so the
            // ClientResultException arms below skip any chain that holds an AzureFoundryProviderException.
            _ when FindInChain<AzureFoundryProviderException>(exception) is { Kind: AzureFoundryProviderErrorKind.AuthFailed or AzureFoundryProviderErrorKind.AuthRequired } azureAuth =>
                new FailureClassification(FailureCategory.ProviderAuthFailed, azureAuth.Message),
            // An OpenAI-compatible provider or gateway rejecting a call by status. 400 and 5xx fall through to the grammar, capability and model-load
            // arms, which read a wrapper's text; the 5xx arm follows them. A 403 refusing the model by name is not a bad credential.
            _ when FindProviderClientResult(exception) is { Status: 403 } forbidden
                   && ReadBodyDetail(forbidden) is { Message: { } forbiddenMessage } forbiddenDetail
                   && RefusesTheModel(forbiddenMessage) =>
                new FailureClassification(FailureCategory.ModelUnavailable,
                    $"The provider does not offer the selected model to this connection ({forbiddenDetail.Text}). Check the model id on the connection."),
            _ when FindProviderClientResult(exception) is { Status: 401 or 403 } authFailed =>
                new FailureClassification(FailureCategory.ProviderAuthFailed, AppendDetail(ProviderAuthFailedMessage, authFailed)),
            _ when FindProviderClientResult(exception) is { Status: 404 } =>
                new FailureClassification(FailureCategory.ModelUnavailable, ProviderModelNotOfferedMessage),
            _ when FindProviderClientResult(exception) is { Status: 429 } rateLimited =>
                new FailureClassification(FailureCategory.ProviderRateLimited, BuildRateLimitedMessage(rateLimited)),
            HttpRequestException httpRequestException when httpRequestException.StatusCode == HttpStatusCode.NotFound =>
                new FailureClassification(FailureCategory.ModelUnavailable, ModelUnavailableMessage),
            // llama-server reports an uncompilable tool-schema grammar as an HTTP 400, so this arm MUST match before the ReportsModelLoadFailure and generic
            // HttpRequestException arms that would swallow it. Reuses ModelCapabilityUnsupported; MCP servers supply schemas the node does not control, so it stays reachable.
            _ when ReportsToolGrammarPreparationFailure(exception) =>
                new FailureClassification(FailureCategory.ModelCapabilityUnsupported, ToolCallingPreparationFailedMessage),
            // A chat-template render failure is also an HTTP 400, so it too matches ahead of the generic HttpRequestException arm.
            _ when ReportsChatTemplateFailure(exception) =>
                new FailureClassification(FailureCategory.ModelCapabilityUnsupported, ChatTemplateRejectedMessage),
            // Unmask the Ollama capability and load errors that would otherwise collapse to the generic ProviderUnreachable. The capability check matches
            // BEFORE the HTTP-500 load check, so a 400 "does not support ..." is always a capability problem rather than a load failure, whatever status it carries.
            _ when ResolveUnsupportedCapabilityMessage(exception) is { } capabilityMessage =>
                new FailureClassification(FailureCategory.ModelCapabilityUnsupported, capabilityMessage),
            _ when ReportsModelLoadFailure(exception) =>
                new FailureClassification(FailureCategory.ModelLoadFailed, ModelLoadFailedMessage),
            // The provider answered with a 5xx, so "unreachable" would mislead: name the status. The category stays ProviderUnreachable for the UI contract.
            _ when FindProviderClientResult(exception) is { Status: >= 500 and <= 599 } serverError =>
                new FailureClassification(FailureCategory.ProviderUnreachable, BuildServerErrorMessage(serverError)),
            HttpRequestException => new FailureClassification(FailureCategory.ProviderUnreachable, ProviderUnavailableMessage),
            // The model server closed the stream mid-answer: a local llama-server that exited (crash, kill, out of memory). Its framework
            // text "The response ended prematurely. (ResponseEnded)" said nothing the operator could act on; a resend reloads the model.
            _ when FindInChain<HttpIOException>(exception) is { HttpRequestError: HttpRequestError.ResponseEnded } =>
                new FailureClassification(FailureCategory.ProviderUnreachable, ResponseEndedMessage),
            _ when IsAgentRuntimeException(exception) => new FailureClassification(FailureCategory.AgentRuntime, RedactAgentRuntimeMessage(exception.Message)),
            _ => new FailureClassification(FailureCategory.Unexpected, TruncateUnexpectedMessage(exception.Message))
        };
    }

    /// <summary>
    ///     The capability-specific fixed, path-free message when the exception chain reports an Ollama capability
    ///     rejection, or <see langword="null" /> when none is present.
    /// </summary>
    /// <remarks>
    ///     The rejection is an HTTP 400 whose body contains "does not support thinking" or "does not support tools".
    ///     OllamaSharp surfaces it as an exception whose <see cref="Exception.Message" /> IS the parsed error string, so a
    ///     substring scan over the chain is robust to framework wrapping. The raw body is never forwarded; only the
    ///     matched capability decides which constant is surfaced.
    /// </remarks>
    private static string? ResolveUnsupportedCapabilityMessage(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("does not support thinking", StringComparison.OrdinalIgnoreCase))
            {
                return ModelDoesNotSupportThinkingMessage;
            }

            if (message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
            {
                return ModelDoesNotSupportToolsMessage;
            }
        }

        return null;
    }

    /// <summary>Whether the exception chain reports a llama.cpp sampler or grammar preparation failure.</summary>
    /// <remarks>
    ///     The GBNF converter could not compile the offered tool schemas, so llama-server rejects the request with an
    ///     HTTP 400 body of "Failed to initialize samplers: failed to parse grammar". The chain is walked because the
    ///     agent framework wraps the transport exception, for the same reason as
    ///     <see cref="ResolveUnsupportedCapabilityMessage" />. Only the match decides the surfaced constant; the raw body
    ///     is never forwarded.
    /// </remarks>
    private static bool ReportsToolGrammarPreparationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("failed to parse grammar", StringComparison.OrdinalIgnoreCase)
                || message.Contains("failed to initialize samplers", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the exception chain reports llama-server failing to render the model's Jinja chat template.</summary>
    /// <remarks>
    ///     Walked for the same reason as <see cref="ReportsToolGrammarPreparationFailure" />, and only for a provider HTTP
    ///     400, so a local exception that merely quotes the text is not reworded. The raw template text is never forwarded.
    /// </remarks>
    private static bool ReportsChatTemplateFailure(Exception exception)
    {
        var templateText = false;
        var badRequest = false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            templateText |= message.Contains("Jinja Exception", StringComparison.OrdinalIgnoreCase)
                            || message.Contains("Unable to generate parser for this template", StringComparison.OrdinalIgnoreCase);
            badRequest |= current is HttpRequestException { StatusCode: HttpStatusCode.BadRequest } or ClientResultException { Status: 400 };
        }

        return templateText && badRequest;
    }

    /// <summary>Whether the exception chain reports an Ollama model-load failure.</summary>
    /// <remarks>
    ///     That is an HTTP 500 — OllamaSharp surfaces it via <see cref="HttpRequestException.StatusCode" />, not the
    ///     body, so no path can leak — or a body mentioning "unable to load model" or "unknown model architecture"
    ///     should one ever reach the message. The surfaced message is a fixed, path-free constant.
    /// </remarks>
    private static bool ReportsModelLoadFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.InternalServerError })
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("unable to load model", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first <see cref="ClientResultException" /> in the chain, unless an Azure provider exception sits anywhere in it.</summary>
    private static ClientResultException? FindProviderClientResult(Exception exception)
    {
        return FindInChain<AzureFoundryProviderException>(exception) is null
            ? FindInChain<ClientResultException>(exception)
            : null;
    }

    private static TException? FindInChain<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>The fixed rate-limit message, plus the response's <c>Retry-After</c> value when it is short and plain.</summary>
    /// <remarks>
    ///     The value is quoted verbatim (delta-seconds or an HTTP-date) only when it fits <see cref="MaxRetryAfterLength" />
    ///     and holds letters, digits, spaces and <c>,:+-.</c>; anything else is dropped, so a hostile header cannot inject text.
    /// </remarks>
    private static string BuildRateLimitedMessage(ClientResultException exception)
    {
        string? retryAfter = null;
        _ = exception.GetRawResponse()?.Headers.TryGetValue("Retry-After", out retryAfter);
        retryAfter = retryAfter?.Trim();

        return string.IsNullOrEmpty(retryAfter)
               || retryAfter.Length > MaxRetryAfterLength
               || !retryAfter.All(static c => char.IsAsciiLetterOrDigit(c) || c is ' ' or ',' or ':' or '+' or '-' or '.')
            ? $"{ProviderRateLimitedMessage}."
            : $"{ProviderRateLimitedMessage}, retry after {retryAfter}.";
    }

    /// <summary>A fixed server-error message naming the HTTP status, plus the response reason phrase when it is short and plain.</summary>
    /// <remarks>
    ///     The phrase is quoted only when it fits <see cref="MaxReasonPhraseLength" /> and holds ASCII letters, digits,
    ///     spaces and <c>.,:-_()</c>; anything else is dropped. The body's sanitized detail is appended; the URL is never surfaced.
    /// </remarks>
    private static string BuildServerErrorMessage(ClientResultException exception)
    {
        var reasonPhrase = exception.GetRawResponse()?.ReasonPhrase?.Trim();
        var statusText = string.IsNullOrEmpty(reasonPhrase)
                         || reasonPhrase.Length > MaxReasonPhraseLength
                         || !reasonPhrase.All(static c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or ',' or ':' or '-' or '_' or '(' or ')')
            ? $"HTTP {exception.Status}"
            : $"HTTP {exception.Status} {reasonPhrase}";

        return AppendDetail($"The provider returned a server error ({statusText}). Retry, or check the connection's gateway.", exception);
    }

    // Whole-word "model" plus a refusal phrase, so "Missing or invalid required header 'X-Model-Token'." stays a credential failure.
    private static bool RefusesTheModel(string message)
    {
        return ModelWordPattern.IsMatch(message)
               && ModelRefusalPhrases.Any(phrase => message.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The message, followed by the response body's sanitized detail in parentheses when it carries one.</summary>
    private static string AppendDetail(string message, ClientResultException exception)
    {
        return ReadBodyDetail(exception).Text is { } detail ? $"{message} ({detail})" : message;
    }

    private static ExternalProviderErrorDetail ReadBodyDetail(ClientResultException exception)
    {
        return ExternalProviderErrorDetail.Parse(exception.GetRawResponse()?.Content?.ToMemory() ?? default);
    }

    private static string RedactAgentRuntimeMessage(string message)
    {
        var sanitizedMessage = FrameworkExceptionNamePattern.Replace(message, string.Empty);
        sanitizedMessage = Regex.Replace(sanitizedMessage, @"\s{2,}", " ", RegexOptions.None, TimeSpan.FromSeconds(2)).Trim(' ', ':', '-', ',', ';');

        return string.IsNullOrWhiteSpace(sanitizedMessage)
            ? "Agent runtime error."
            : $"Agent runtime error: {sanitizedMessage}";
    }

    private static bool IsAgentRuntimeException(Exception exception)
    {
        var type = exception.GetType();
        var fullName = type.FullName ?? string.Empty;

        return fullName.StartsWith("Microsoft.Agents.AI.", StringComparison.Ordinal)
               || string.Equals(type.Name, "AgentException", StringComparison.Ordinal)
               || string.Equals(type.Name, "ChatClientAgentException", StringComparison.Ordinal)
               || messageContainsFrameworkTypeName(exception.Message);

        static bool messageContainsFrameworkTypeName(string message)
        {
            return FrameworkExceptionNamePattern.IsMatch(message);
        }
    }

    private static string TruncateUnexpectedMessage(string message)
    {
        return message.Length > 512 ? message[..512] : message;
    }

    // A terminal failure reduced to what the client may see: the category the UI branches on, and the user-facing
    // message — either one of this file's fixed path-free constants or an already-sanitized exception message.
    internal sealed record FailureClassification(FailureCategory Category, string Message);
}
