namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Globalization;
using System.Runtime.CompilerServices;
using Azure;
using Microsoft.Extensions.AI;

/// <summary>
///     Wraps the inner Azure OpenAI <see cref="IChatClient" /> so an Azure <see cref="RequestFailedException" /> is
///     translated into a typed <see cref="AzureFoundryProviderException" /> with a sanitized message — the chat
///     surface gets a clean error instead of a raw transport crash, and no API key / Entra token can leak into the
///     message.
/// </summary>
/// <remarks>
///     <para>
///         Content-filter blocks arrive as HTTP 400 with error code <c>content_filter</c>; auth failures as HTTP
///         401/403. Both are mapped to <see cref="AzureFoundryProviderErrorKind" /> values; any other
///         <see cref="RequestFailedException" /> is reported as <see cref="AzureFoundryProviderErrorKind.Transport" />.
///     </para>
///     <para>
///         Disposal is left to the base <see cref="DelegatingChatClient" />: the inner MEAI Azure OpenAI client owns the
///         underlying pipeline, and this thin wrapper adds nothing disposable of its own.
///     </para>
/// </remarks>
internal sealed class AzureFoundryErrorTranslatingChatClient : DelegatingChatClient
{
    private const string ContentFilterErrorCode = "content_filter";

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
}
