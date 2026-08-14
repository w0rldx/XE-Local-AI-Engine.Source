namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Net;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed partial class InvocationRunner
{
    private static (FailureCategory Category, string Message) MapFailure(Exception exception)
    {
        return exception switch
        {
            // Admission policies supply sanitized, user-facing refusal text. Surface it verbatim as an ordinary
            // terminal agent-runtime failure; the policy runs after warm-up but before any generation method is called.
            InvocationGenerationRejectedException generationRejected => (FailureCategory.AgentRuntime, generationRejected.Message),
            // Matches BEFORE the generic InvalidOperationException arms below (both derive from InvalidOperationException):
            // a local-default send with no installed GGUF chat model surfaces ModelNotInstalled, not ProviderUnreachable.
            NoChatModelInstalledException => (FailureCategory.ModelNotInstalled, NoChatModelInstalledMessage),
            // An operator force-ejected the model out from under an in-flight turn. Classify as Cancelled (an operator
            // action, not a provider outage) so it is NOT counted as a generic failure and the user sees a truthful
            // "ejected by the operator" message. The exception's message is already user-safe, so it is surfaced verbatim
            // (same treatment as StreamIdleTimeoutException). Reusing Cancelled avoids drifting the generated OpenAPI/zod
            // FailureCategory enum. Matches before the HttpRequestException/agent-runtime arms (its inner is a transport
            // exception, but the OUTER type is matched first).
            LlamaServerModelEjectedException modelEjected => (FailureCategory.Cancelled, modelEjected.Message),
            // The budgeter's hard-stop (history still over budget after truncation): a clean, classified pre-inference
            // failure. The exception's own message IS the fixed, path-free constant (ContextBudgetExceededMessage), so
            // it is surfaced verbatim, same treatment as StreamIdleTimeoutException below.
            ContextBudgetExceededException contextBudgetExceeded => (FailureCategory.ContextWindowExceeded, contextBudgetExceeded.Message),
            // The provider-boundary cumulative-ceiling backstop (a runaway tool/hand-off loop). Reuses the
            // ContextWindowExceeded category (adding a new FailureCategory value would drift the generated OpenAPI/zod
            // client) but carries its own fixed, path-free runaway-loop message. Matches before the generic
            // InvalidOperationException/agent-runtime arms below (it derives from InvalidOperationException).
            ProviderCallBudgetExceededException providerCallBudgetExceeded => (FailureCategory.ContextWindowExceeded, providerCallBudgetExceeded.Message),
            // A single irreducible provider round (its pinned set alone exceeds the context window): the per-round
            // boundary fails it before the provider is called. Reuses ContextWindowExceeded (a new FailureCategory value
            // would drift the generated OpenAPI/zod client) but carries its own fixed, path-free message; the bounded
            // token/window diagnostics it also holds are logged server-side, never surfaced. Matches before the generic
            // InvalidOperationException/agent-runtime arms below (it derives from InvalidOperationException).
            ProviderContextWindowExceededException providerContextWindowExceeded => (FailureCategory.ContextWindowExceeded, providerContextWindowExceeded.Message),
            // An approval was required in a run that cannot obtain one (unattended). The exception's message is our own
            // fixed-shape reason carrying nothing but a tool name, so it is surfaced verbatim (same treatment as
            // StreamIdleTimeoutException) — that reason IS the value of this failure over the generic approval timeout
            // it replaces. Classified as AgentRuntime rather than a new FailureCategory value, which would drift the
            // generated OpenAPI/zod client. Matches before the generic InvalidOperationException arms below (it derives
            // from InvalidOperationException).
            ApprovalUnavailableException approvalUnavailable => (FailureCategory.AgentRuntime, approvalUnavailable.Message),
            // A tripped circuit breaker: surface a fixed, retry-soon message rather than the generic ProviderUnreachable
            // (the endpoint is likely recovering, not permanently down). Matches before the StreamIdle/TimeoutException
            // arm because it is not a TimeoutException.
            ProviderCircuitOpenException => (FailureCategory.ProviderUnreachable, ProviderTemporarilyUnavailableMessage),
            // The inter-chunk stall carries the watchdog's own message, which is already a fixed, path-free constant
            // that names which timeout fired — so it is surfaced verbatim. Matches BEFORE the generic TimeoutException
            // arm because it derives from it.
            StreamIdleTimeoutException streamIdleTimeout => (FailureCategory.Timeout, streamIdleTimeout.Message),
            // Any other TimeoutException (the invocation-level cancel-after, or an HTTP client timeout surfacing as a
            // bare TimeoutException) carries a framework message that can name hosts/paths/internals and is unbounded, so
            // it is collapsed to a fixed, path-free constant rather than forwarded.
            TimeoutException => (FailureCategory.Timeout, TimedOutMessage),
            WorkerToolCallException => (FailureCategory.AgentToolCall, AgentToolCallFailureMessage),
            NotSupportedException notSupportedException => (FailureCategory.AgentRuntime, RedactAgentRuntimeMessage(notSupportedException.Message)),
            InvalidOperationException invalidOperationException when invalidOperationException.Message.Contains("Response size exceeded", StringComparison.Ordinal) =>
                (FailureCategory.Unexpected, invalidOperationException.Message),
            HttpRequestException httpRequestException when httpRequestException.StatusCode == HttpStatusCode.NotFound =>
                (FailureCategory.ModelUnavailable, ModelUnavailableMessage),
            // llama-server could not compile the constrained-decoding grammar for the offered tool schemas. It reports
            // this as an HTTP 400, so this arm MUST match before the ReportsModelLoadFailure and generic
            // HttpRequestException arms below, which would otherwise swallow it into ModelLoadFailed/ProviderUnreachable
            // (today it escapes all of them and surfaces the raw provider body as Unexpected). Reuses the existing
            // ModelCapabilityUnsupported category with its own fixed, path-free message — adding a new FailureCategory
            // value would drift the generated OpenAPI/zod client. Still reachable after the node's own tool schemas are
            // shrunk to compile, because MCP servers supply third-party schemas the node does not control.
            _ when ReportsToolGrammarPreparationFailure(exception) =>
                (FailureCategory.ModelCapabilityUnsupported, ToolCallingPreparationFailedMessage),
            // Unmask the Ollama capability/load errors that would otherwise collapse to the generic ProviderUnreachable.
            // The capability check matches BEFORE the HTTP-500 load check so a 400 "does not support ..." is always
            // classified as a capability problem (not a load failure), regardless of the carried status code.
            _ when ResolveUnsupportedCapabilityMessage(exception) is { } capabilityMessage =>
                (FailureCategory.ModelCapabilityUnsupported, capabilityMessage),
            _ when ReportsModelLoadFailure(exception) =>
                (FailureCategory.ModelLoadFailed, ModelLoadFailedMessage),
            HttpRequestException => (FailureCategory.ProviderUnreachable, ProviderUnavailableMessage),
            _ when IsAgentRuntimeException(exception) => (FailureCategory.AgentRuntime, RedactAgentRuntimeMessage(exception.Message)),
            _ => (FailureCategory.Unexpected, TruncateUnexpectedMessage(exception.Message))
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

    private static bool IsLocalLoopbackInvocation(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        return package.RequestedCapabilities?.Any(static capability => string.Equals(capability, LocalChatLoopbackDefaults.RequestedCapability, StringComparison.Ordinal)) == true;
    }
}
