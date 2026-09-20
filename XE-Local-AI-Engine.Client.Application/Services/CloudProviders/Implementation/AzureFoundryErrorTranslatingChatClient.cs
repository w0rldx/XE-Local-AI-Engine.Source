namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Microsoft.Extensions.AI;

/// <summary>
///     Wraps the inner chat client so an Azure <see cref="RequestFailedException" />, the v1 surface's
///     <see cref="ClientResultException" /> or an Entra ID <see cref="AuthenticationFailedException" /> becomes a typed
///     <see cref="AzureFoundryProviderException" />.
/// </summary>
/// <remarks>
///     The message is sanitized: no API key or Entra token can leak into it, and it never carries a stack trace or the exception's
///     full content. Content-filter blocks (HTTP 400, <c>content_filter</c>) and 401/403 auth failures map to their own
///     <see cref="AzureFoundryProviderErrorKind" /> values; anything else is <see cref="AzureFoundryProviderErrorKind.Transport" />.
///     Disposal is left to the base <see cref="DelegatingChatClient" />: the inner MEAI client owns the pipeline and this wrapper
///     owns nothing disposable. Mapping table and the AADSTS line-selection rule: docs/wiki/03-local-runtime-and-providers.md, "Azure Foundry error translation".
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
            return await base.GetResponseAsync(messages, options, cancellationToken);
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
                    if (!await enumerator.MoveNextAsync())
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
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>Maps an Azure <see cref="RequestFailedException" /> to a typed provider error.</summary>
    /// <remarks>
    ///     The message carries only the status code and the Azure error code / body detail — never the exception's full
    ///     content, which could echo request fields, and never a credential value.
    /// </remarks>
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

    /// <summary>Maps the v1 API surface's <see cref="ClientResultException" /> to the same typed provider error shape.</summary>
    /// <remarks>
    ///     The plain OpenAI SDK throws it in place of the Azure-specific <see cref="RequestFailedException" /> above, and
    ///     it carries no <c>ErrorCode</c> property, so content-filter detection here reads the response body's
    ///     <c>error.code</c> instead.
    /// </remarks>
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

    /// <summary>The response body's error code and message (an APIM policy failure detail, say), when the body is a JSON object.</summary>
    /// <remarks>
    ///     The accepted shapes are <c>{ "error": { "code", "message" } }</c> and a bare <c>{ "code", "message" }</c>.
    ///     Never throws on a non-JSON or unshaped body: a gateway can return plain text or HTML on a 5xx, and that must
    ///     never surface raw (it could echo request data) nor crash the translation.
    /// </remarks>
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

    /// <summary>Collapses a possibly multi-line body message to one line and caps its length.</summary>
    /// <remarks>
    ///     The same cap as <see cref="SanitizeAuthenticationFailureMessage" /> below, reused here so neither lets an
    ///     oversized or newline-bearing body value distort the surfaced error.
    /// </remarks>
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

    /// <summary>Maps an Entra ID token-acquisition failure to <c>AuthFailed</c>, the same category as an Azure OpenAI 401/403.</summary>
    /// <remarks>
    ///     A wrong secret/tenant/client id, a rejected scope, a disabled app or missing consent all mean the credential
    ///     the connection is configured with was rejected. See <see cref="SanitizeAuthenticationFailureMessage" /> for
    ///     the line-selection rule; the result never carries a stack trace or any token material, and Azure.Identity's
    ///     own message never includes the secret/token value.
    /// </remarks>
    private static AzureFoundryProviderException Translate(AuthenticationFailedException exception)
    {
        return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthFailed,
            $"Azure Foundry rejected the Entra ID credentials: {SanitizeAuthenticationFailureMessage(exception)}",
            exception);
    }

    /// <summary>Selects the actionable line out of an <see cref="AuthenticationFailedException" /> chain and drops everything else.</summary>
    /// <remarks>
    ///     The outer <c>Message</c> is a generic preamble ("ClientSecretCredential authentication failed: ") with the actionable AADSTS reason on an INNER
    ///     exception (MSAL's <c>MsalServiceException</c> via <c>InnerException</c>; the <c> ---&gt; </c> chain in a log is how .NET renders nesting, not one
    ///     multi-line message). This collects every message's lines, keeps the outermost first line for credential-type context, and appends the first line
    ///     anywhere in the chain containing "AADSTS", or just the first line when none exists. Every other line — stack traces, MSAL correlation ids — is
    ///     dropped before the length cap.
    /// </remarks>
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
