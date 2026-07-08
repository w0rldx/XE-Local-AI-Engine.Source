namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Globalization;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Identity;
using Microsoft.Extensions.AI;

/// <summary>
///     Wraps the inner Azure OpenAI <see cref="IChatClient" /> so an Azure <see cref="RequestFailedException" /> or
///     an Entra ID <see cref="AuthenticationFailedException" /> is translated into a typed
///     <see cref="AzureFoundryProviderException" /> with a sanitized message — the chat surface gets a clean error
///     instead of a raw transport crash or MSAL stack, and no API key / Entra token can leak into the message.
/// </summary>
/// <remarks>
///     <para>
///         Content-filter blocks arrive as HTTP 400 with error code <c>content_filter</c>; auth failures as HTTP
///         401/403. Both are mapped to <see cref="AzureFoundryProviderErrorKind" /> values; any other
///         <see cref="RequestFailedException" /> is reported as <see cref="AzureFoundryProviderErrorKind.Transport" />.
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
    private const int MaxAuthenticationFailureMessageLength = 300;

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

                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Maps an Azure RequestFailedException to a typed provider error. The message is sanitized — it carries only the
    // status code and the Azure error code, never the exception's full content (which could echo request fields) and
    // never any credential value.
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

        return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Transport,
            $"The Azure Foundry endpoint returned an error (HTTP {exception.Status.ToString(CultureInfo.InvariantCulture)}).",
            exception);
    }

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

        return sanitized.Length > MaxAuthenticationFailureMessageLength
            ? sanitized[..MaxAuthenticationFailureMessageLength]
            : sanitized;
    }
}
