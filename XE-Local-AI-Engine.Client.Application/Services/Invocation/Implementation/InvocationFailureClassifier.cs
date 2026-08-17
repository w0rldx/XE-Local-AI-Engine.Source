namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Net;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Maps a terminal invocation exception to the pair the client is allowed to see: the <see cref="FailureCategory" />
///     the UI branches on, and a user-facing message that is either one of this type's fixed, path-free constants or an
///     already-sanitized exception message. Pure functions over an <see cref="Exception" /> — no invocation state — so the
///     switch-arm ORDER in <see cref="MapFailure" /> is the whole contract and is pinned by
///     <c>InvocationRunnerTests</c>.
/// </summary>
internal static class InvocationFailureClassifier
{
    private const string AgentToolCallFailureMessage = "Worker tool execution failed.";

    // A tool call that ran out of time waiting for its RESULT (TurnPolicy.ToolResultTimeout, i.e. the package's
    // ToolCallTimeoutSeconds or the node-global pending-tool-call age). Split out of AgentToolCallFailureMessage so a
    // tool-side timeout is attributable rather than reading like an ordinary tool error. Carries no tool name, so it
    // stays as path-free as the constant it replaces on this arm.
    private const string ToolCallTimedOutMessage = "A tool call timed out waiting for its result.";
    private const string ModelUnavailableMessage = "Selected model is not installed on this node.";
    private const string ProviderUnavailableMessage = "Provider unreachable.";

    // Surfaced when an endpoint's circuit breaker is open (recent consecutive transient failures): a fixed, path-free
    // message that tells the operator to retry shortly rather than reporting a hard provider outage.
    private const string ProviderTemporarilyUnavailableMessage = "Provider temporarily unavailable. Please retry shortly.";
    private const string ModelDoesNotSupportThinkingMessage = "This model does not support reasoning.";

    private const string ModelDoesNotSupportToolsMessage = "This model does not support tool calling.";

    // llama-server failed to COMPILE the constrained-decoding grammar for the offered tool schemas ("Failed to
    // initialize samplers: failed to parse grammar"). The model is tool-capable — the schema set is what it could not
    // be prepared for — so this must never claim the model lacks tool calling. Fixed and path-free: the provider body
    // is never forwarded.
    private const string ToolCallingPreparationFailedMessage =
        "The model could not be prepared for tool calling with the current tool set. Retry with tools turned off, or select a different model.";

    // A provider HTTP 500 means the model was reached but failed to load OR run (e.g. an Ollama build too old for the
    // model architecture, or an out-of-memory at load). Phrased to cover both so it never falsely asserts a permanent
    // model defect, while still being far more actionable than the generic "Provider unreachable.".
    private const string ModelLoadFailedMessage = "The model could not be loaded or run on the provider.";

    // A "Local runtime default" send found no installed GGUF chat model to route to. Surfaced instead of the generic
    // "Provider unreachable." so the operator gets an actionable next step (pull a GGUF model) rather than a dead-end.
    private const string NoChatModelInstalledMessage = "No chat model installed. Pull a GGUF model to start chatting.";

    // A generic (non-inter-chunk) timeout: the invocation-level cancel-after or an HTTP client timeout. Its framework
    // message can name hosts/paths and is unbounded, so a fixed, path-free constant is surfaced in its place.
    private const string TimedOutMessage = "The operation timed out.";

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
            // Matches BEFORE the generic InvalidOperationException arms below (both derive from InvalidOperationException):
            // a local-default send with no installed GGUF chat model surfaces ModelNotInstalled, not ProviderUnreachable.
            NoChatModelInstalledException => new FailureClassification(FailureCategory.ModelNotInstalled, NoChatModelInstalledMessage),
            // An operator force-ejected the model out from under an in-flight turn. Classify as Cancelled (an operator
            // action, not a provider outage) so it is NOT counted as a generic failure and the user sees a truthful
            // "ejected by the operator" message. The exception's message is already user-safe, so it is surfaced verbatim
            // (same treatment as StreamIdleTimeoutException). Reusing Cancelled avoids drifting the generated OpenAPI/zod
            // FailureCategory enum. Matches before the HttpRequestException/agent-runtime arms (its inner is a transport
            // exception, but the OUTER type is matched first).
            LlamaServerModelEjectedException modelEjected => new FailureClassification(FailureCategory.Cancelled, modelEjected.Message),
            // The budgeter's hard-stop (history still over budget after truncation): a clean, classified pre-inference
            // failure. The exception's own message IS the fixed, path-free constant (ContextBudgetExceededMessage), so
            // it is surfaced verbatim, same treatment as StreamIdleTimeoutException below.
            ContextBudgetExceededException contextBudgetExceeded => new FailureClassification(FailureCategory.ContextWindowExceeded, contextBudgetExceeded.Message),
            // The provider-boundary cumulative-ceiling backstop (a runaway tool/hand-off loop). Reuses the
            // ContextWindowExceeded category (adding a new FailureCategory value would drift the generated OpenAPI/zod
            // client) but carries its own fixed, path-free runaway-loop message. Matches before the generic
            // InvalidOperationException/agent-runtime arms below (it derives from InvalidOperationException).
            ProviderCallBudgetExceededException providerCallBudgetExceeded => new FailureClassification(FailureCategory.ContextWindowExceeded, providerCallBudgetExceeded.Message),
            // A single irreducible provider round (its pinned set alone exceeds the context window): the per-round
            // boundary fails it before the provider is called. Reuses ContextWindowExceeded (a new FailureCategory value
            // would drift the generated OpenAPI/zod client) but carries its own fixed, path-free message; the bounded
            // token/window diagnostics it also holds are logged server-side, never surfaced. Matches before the generic
            // InvalidOperationException/agent-runtime arms below (it derives from InvalidOperationException).
            ProviderContextWindowExceededException providerContextWindowExceeded => new FailureClassification(FailureCategory.ContextWindowExceeded, providerContextWindowExceeded.Message),
            // An approval was required in a run that cannot obtain one (unattended). The exception's message is our own
            // fixed-shape reason carrying nothing but a tool name, so it is surfaced verbatim (same treatment as
            // StreamIdleTimeoutException) — that reason IS the value of this failure over the generic approval timeout
            // it replaces. Classified as AgentRuntime rather than a new FailureCategory value, which would drift the
            // generated OpenAPI/zod client. Matches before the generic InvalidOperationException arms below (it derives
            // from InvalidOperationException).
            ApprovalUnavailableException approvalUnavailable => new FailureClassification(FailureCategory.AgentRuntime, approvalUnavailable.Message),
            // A tripped circuit breaker: surface a fixed, retry-soon message rather than the generic ProviderUnreachable
            // (the endpoint is likely recovering, not permanently down). Matches before the StreamIdle/TimeoutException
            // arm because it is not a TimeoutException.
            ProviderCircuitOpenException => new FailureClassification(FailureCategory.ProviderUnreachable, ProviderTemporarilyUnavailableMessage),
            // The inter-chunk stall carries the watchdog's own message, which is already a fixed, path-free constant
            // that names which timeout fired — so it is surfaced verbatim. Matches BEFORE the generic TimeoutException
            // arm because it derives from it.
            StreamIdleTimeoutException streamIdleTimeout => new FailureClassification(FailureCategory.Timeout, streamIdleTimeout.Message),
            // Any other TimeoutException (the invocation-level cancel-after, or an HTTP client timeout surfacing as a
            // bare TimeoutException) carries a framework message that can name hosts/paths/internals and is unbounded, so
            // it is collapsed to a fixed, path-free constant rather than forwarded.
            TimeoutException => new FailureClassification(FailureCategory.Timeout, TimedOutMessage),
            // A tool call the runner itself timed out (ToolResultTimeout / the pending-tool-call cleanup) wraps the
            // timeout as its inner exception. Matched BEFORE the generic tool-call arm so a tool-side timeout is
            // attributable instead of collapsing into the same "Worker tool execution failed." every tool error uses.
            WorkerToolCallException { InnerException: TimeoutException or OperationCanceledException } =>
                new FailureClassification(FailureCategory.AgentToolCall, ToolCallTimedOutMessage),
            WorkerToolCallException => new FailureClassification(FailureCategory.AgentToolCall, AgentToolCallFailureMessage),
            NotSupportedException notSupportedException => new FailureClassification(FailureCategory.AgentRuntime, RedactAgentRuntimeMessage(notSupportedException.Message)),
            InvalidOperationException invalidOperationException when invalidOperationException.Message.Contains("Response size exceeded", StringComparison.Ordinal) =>
                new FailureClassification(FailureCategory.Unexpected, invalidOperationException.Message),
            HttpRequestException httpRequestException when httpRequestException.StatusCode == HttpStatusCode.NotFound =>
                new FailureClassification(FailureCategory.ModelUnavailable, ModelUnavailableMessage),
            // llama-server could not compile the constrained-decoding grammar for the offered tool schemas. It reports
            // this as an HTTP 400, so this arm MUST match before the ReportsModelLoadFailure and generic
            // HttpRequestException arms below, which would otherwise swallow it into ModelLoadFailed/ProviderUnreachable
            // (today it escapes all of them and surfaces the raw provider body as Unexpected). Reuses the existing
            // ModelCapabilityUnsupported category with its own fixed, path-free message — adding a new FailureCategory
            // value would drift the generated OpenAPI/zod client. Still reachable after the node's own tool schemas are
            // shrunk to compile, because MCP servers supply third-party schemas the node does not control.
            _ when ReportsToolGrammarPreparationFailure(exception) =>
                new FailureClassification(FailureCategory.ModelCapabilityUnsupported, ToolCallingPreparationFailedMessage),
            // Unmask the Ollama capability/load errors that would otherwise collapse to the generic ProviderUnreachable.
            // The capability check matches BEFORE the HTTP-500 load check so a 400 "does not support ..." is always
            // classified as a capability problem (not a load failure), regardless of the carried status code.
            _ when ResolveUnsupportedCapabilityMessage(exception) is { } capabilityMessage =>
                new FailureClassification(FailureCategory.ModelCapabilityUnsupported, capabilityMessage),
            _ when ReportsModelLoadFailure(exception) =>
                new FailureClassification(FailureCategory.ModelLoadFailed, ModelLoadFailedMessage),
            HttpRequestException => new FailureClassification(FailureCategory.ProviderUnreachable, ProviderUnavailableMessage),
            _ when IsAgentRuntimeException(exception) => new FailureClassification(FailureCategory.AgentRuntime, RedactAgentRuntimeMessage(exception.Message)),
            _ => new FailureClassification(FailureCategory.Unexpected, TruncateUnexpectedMessage(exception.Message))
        };
    }

    /// <summary>
    ///     Returns the capability-specific, fixed, path-free message when the exception (or any inner exception) reports an
    ///     Ollama capability rejection — an HTTP 400 whose body contains "does not support thinking" or "does not support
    ///     tools" — or <c>null</c> when no capability rejection is present. OllamaSharp surfaces this as an
    ///     <c>OllamaException</c>/<c>ModelDoesNotSupportToolsException</c> whose <see cref="Exception.Message" /> IS the
    ///     parsed error string, so a substring scan over the exception chain is robust to framework wrapping. The raw body
    ///     is never forwarded; only the matched capability decides which constant is surfaced.
    /// </summary>
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

    /// <summary>
    ///     True when the exception (or any inner exception) reports a llama.cpp sampler/grammar preparation failure — the
    ///     GBNF converter could not compile the offered tool schemas, so llama-server rejects the request with an HTTP 400
    ///     body of "Failed to initialize samplers: failed to parse grammar". The chain is walked because the agent
    ///     framework wraps the transport exception (same reason as
    ///     <see cref="ResolveUnsupportedCapabilityMessage" />). Only the match decides the surfaced constant; the raw body
    ///     is never forwarded.
    /// </summary>
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

    /// <summary>
    ///     True when the exception (or any inner exception) reports an Ollama model-load failure — an HTTP 500 (OllamaSharp
    ///     surfaces this via <see cref="HttpRequestException.StatusCode" />, NOT the body, so no path can leak), or a body
    ///     mentioning "unable to load model" / "unknown model architecture" should one ever reach the message. The
    ///     surfaced message is a fixed, path-free constant.
    /// </summary>
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
