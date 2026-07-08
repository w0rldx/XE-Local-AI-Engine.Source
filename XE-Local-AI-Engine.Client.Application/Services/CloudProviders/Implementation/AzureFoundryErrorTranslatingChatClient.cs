namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Microsoft.Extensions.AI;

/// <summary>
///     Wraps the inner chat client so an Azure <see cref="RequestFailedException" />, the v1 API surface's
///     <see cref="ClientResultException" />, or an Entra ID <see cref="AuthenticationFailedException" /> is
///     translated into a typed <see cref="AzureFoundryProviderException" /> with a sanitized message — the chat
///     surface gets a clean error instead of a raw transport crash or MSAL stack, and no API key / Entra token can
///     leak into the message.
/// </summary>
/// <remarks>
///     <para>
///         Content-filter blocks arrive as HTTP 400 with error code <c>content_filter</c>; auth failures as HTTP
///         401/403. Both are mapped to <see cref="AzureFoundryProviderErrorKind" /> values; any other
///         <see cref="RequestFailedException" /> / <see cref="ClientResultException" /> is reported as
///         <see cref="AzureFoundryProviderErrorKind.Transport" />, with any JSON <c>error.message</c> found on the
///         response body appended to the generic HTTP-status message (see <c>ExtractErrorBodyDetail</c>) — e.g. a
///         gateway policy failure surfaces its own detail instead of a bare status code.
///     </para>
///     <para>
///         An Entra ID token request (raised lazily, per call, by <c>EntraBearerTokenPipelinePolicy</c>) that Azure
///         AD rejects surfaces as <see cref="AuthenticationFailedException" /> instead — a different exception type
///         than the Azure OpenAI transport uses, so it needs its own translation path. The AADSTS reason usually
///         lives on the <em>inner</em> exception (e.g. MSAL's <c>MsalServiceException</c>), not on the outer
///         <see cref="AuthenticationFailedException.Message" /> itself, so the message keeps the outer exception's
///         first line (credential-type context) plus the first line containing an AADSTS code found anywhere in
///         the <see cref="Exception.InnerException" /> chain, both capped — never a stack trace.
///     </para>
///     <para>
///         Disposal is left to the base <see cref="DelegatingChatClient" />: the inner MEAI Azure OpenAI client owns the
///         underlying pipeline, and this thin wrapper adds nothing disposable of its own.
///     </para>
/// </remarks>
internal sealed class AzureFoundryErrorTranslatingChatClient : DelegatingChatClient
{
    private const string ContentFilterErrorCode = "content_filter";
    private const int MaxSanitizedDetailLength = 300;
    private static readonly char[] NewlineSeparators = ['\n', '\r'];

    public AzureFoundryErrorTranslatingChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception)
        {
            throw Translate(exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw Translate(exception);
        }
        catch (ClientResultException exception)
        {
            throw Translate(exception);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // The Azure RequestFailedException can surface during enumeration (the SSE stream opens lazily), so the
        // translation has to wrap each MoveNextAsync — a try/catch cannot straddle a yield.
        var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }

                    update = enumerator.Current;
                }
                catch (RequestFailedException exception)
                {
                    throw Translate(exception);
                }
                catch (AuthenticationFailedException exception)
                {
                    throw Translate(exception);
                }
                catch (ClientResultException exception)
                {
                    throw Translate(exception);
                }

                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Maps an Azure RequestFailedException to a typed provider error. The message carries only the status code, the
    // Azure error code / body detail, and never the exception's full content (which could echo request fields) or
    // any credential value.
    private static AzureFoundryProviderException Translate(RequestFailedException exception)
    {
        if (exception.Status == 400
            && string.Equals(exception.ErrorCode, ContentFilterErrorCode, StringComparison.OrdinalIgnoreCase))
        {
            return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.ContentFiltered,
                "The request was blocked by the Azure content filter.",
                exception);
        }

        if (exception.Status is 401 or 403)
        {
            return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthFailed,
                "Azure Foundry rejected the credentials (check the API key or the managed-identity RBAC role).",
                exception);
        }

        var detail = ExtractErrorBodyDetail(exception.GetRawResponse()?.Content);
        return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Transport,
            BuildTransportMessage(exception.Status, detail.Message),
            exception);
    }

    // Maps a plain OpenAI SDK System.ClientModel.ClientResultException — the v1 API surface's transport exception,
    // thrown in place of the Azure-specific RequestFailedException above — to the same typed provider error shape.
    // ClientResultException carries no ErrorCode property (unlike RequestFailedException), so content-filter
    // detection here reads the response body's "error.code" instead.
    private static AzureFoundryProviderException Translate(ClientResultException exception)
    {
        var detail = ExtractErrorBodyDetail(exception.GetRawResponse()?.Content);

        if (exception.Status == 400 && string.Equals(detail.Code, ContentFilterErrorCode, StringComparison.OrdinalIgnoreCase))
        {
            return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.ContentFiltered,
                "The request was blocked by the Azure content filter.",
                exception);
        }

        if (exception.Status is 401 or 403)
        {
            return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthFailed,
                "Azure Foundry rejected the credentials (check the API key or the managed-identity RBAC role).",
                exception);
        }

        return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Transport,
            BuildTransportMessage(exception.Status, detail.Message),
            exception);
    }

    private static string BuildTransportMessage(int status, string? detailMessage)
    {
        var baseMessage = $"The Azure Foundry endpoint returned an error (HTTP {status.ToString(CultureInfo.InvariantCulture)}).";
        return detailMessage is null ? baseMessage : $"{baseMessage} {detailMessage}";
    }

    // The response body's error code and message (e.g. an APIM policy failure detail), when the body is a JSON
    // object shaped either `{ "error": { "code", "message" } }` or a bare `{ "code", "message" }`. Never throws on a
    // non-JSON or unshaped body — a gateway can return plain text or HTML on a 5xx, and that must never surface
    // raw (it could echo request data) nor crash the translation.
    private static ErrorBodyDetail ExtractErrorBodyDetail(BinaryData? content)
    {
        if (content is null || content.ToMemory().IsEmpty)
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToMemory());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var errorElement = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            var code = TryGetString(errorElement, "code");
            var message = SanitizeSingleLine(TryGetString(errorElement, "message"));
            return new ErrorBodyDetail(code, message);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    // Collapses a (possibly multi-line) body message to one line and caps its length — the same shape as
    // SanitizeAuthenticationFailureMessage's cap below, reused here so both never let an oversized or newline-bearing
    // body value distort the surfaced error.
    private static string? SanitizeSingleLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var singleLine = string.Join(' ', value.Split(NewlineSeparators).Select(static line => line.Trim()).Where(static line => line.Length > 0));
        return singleLine.Length > MaxSanitizedDetailLength
            ? singleLine[..MaxSanitizedDetailLength]
            : singleLine;
    }

    private readonly record struct ErrorBodyDetail(string? Code, string? Message);

    // Maps an Entra ID token-acquisition failure (wrong secret/tenant/client id, a rejected scope, disabled app,
    // missing consent, etc.) to AuthFailed — the same category as an Azure OpenAI 401/403 — since both mean the
    // credential the connection is configured with was rejected. See SanitizeAuthenticationFailureMessage for the
    // line-selection rule; the result never carries a stack trace or any token material — Azure.Identity's own
    // message never includes the secret/token value.
    private static AzureFoundryProviderException Translate(AuthenticationFailedException exception)
    {
        return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthFailed,
            $"Azure Foundry rejected the Entra ID credentials: {SanitizeAuthenticationFailureMessage(exception)}",
            exception);
    }

    // The outer AuthenticationFailedException.Message is typically just a generic preamble — e.g.
    // "ClientSecretCredential authentication failed: " — with the actionable AADSTS reason living on an INNER
    // exception's message instead (MSAL's MsalServiceException, reached via InnerException; the " ---> " chain
    // seen in a logged ToString() is just how .NET renders nested exceptions, not a single multi-line Message).
    // This walks the InnerException chain collecting every message's lines, keeps the outermost exception's first
    // line for credential-type context, and appends the first line anywhere in the chain that contains "AADSTS"
    // (falling back to just the first line when no AADSTS line exists anywhere — e.g. a local/offline failure).
    // Every other line (stack traces, MSAL correlation ids, anything else) is dropped before the length cap.
    private static string SanitizeAuthenticationFailureMessage(Exception exception)
    {
        var lines = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            lines.AddRange(current.Message
                .Split('\n')
                .Select(static line => line.TrimEnd('\r').Trim())
                .Where(static line => line.Length > 0));
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var firstLine = lines[0];
        var aadstsLine = lines.FirstOrDefault(static line => line.Contains("AADSTS", StringComparison.Ordinal));

        var sanitized = aadstsLine is null || string.Equals(aadstsLine, firstLine, StringComparison.Ordinal)
            ? firstLine
            : $"{firstLine} {aadstsLine}";

        return sanitized.Length > MaxSanitizedDetailLength
            ? sanitized[..MaxSanitizedDetailLength]
            : sanitized;
    }
}
