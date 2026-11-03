namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using System.Text;
using System.Text.Json;

/// <summary>Empties an error response whose body echoes a credential the connection sends, leaving its status and headers.</summary>
/// <remarks>
///     Here because only the connection knows its credentials, and everything after the transport reads the body: the classifier's quoted
///     detail, and the SDK's exception message, which quotes it raw and reaches logs and the operator's fallback text. A credential of
///     letters alone passes any word grammar, so sanitizing later cannot catch it. A successful response is never read: it may be a stream.
/// </remarks>
public sealed class SecretEchoGuardHandler : DelegatingHandler
{
    private readonly IReadOnlyList<string> _secrets;

    /// <param name="secrets">The connection's credential values. Plain header values do not belong here: a reason quoting the project must survive.</param>
    /// <param name="innerHandler">The next handler in the chain.</param>
    public SecretEchoGuardHandler(IReadOnlyList<string> secrets, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _secrets = secrets;
    }

    /// <summary>Whether a body carries one of the values, ignoring case, in its raw text or in any JSON string once decoded.</summary>
    public static bool Echoes(ReadOnlyMemory<byte> body, IReadOnlyList<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var values = secrets.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (values.Length == 0 || body.IsEmpty)
        {
            return false;
        }

        if (Carries(Encoding.UTF8.GetString(body.Span), values))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return Carries(document.RootElement, values);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && Echoes(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), _secrets))
        {
            response.Content.Dispose();
            response.Content = new ByteArrayContent([]);
        }

        return response;
    }

    private static bool Carries(string text, string[] values)
    {
        return values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Carries(JsonElement element, string[] values)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => Carries(element.GetString() ?? string.Empty, values),
            JsonValueKind.Array => element.EnumerateArray().Any(item => Carries(item, values)),
            JsonValueKind.Object => element.EnumerateObject().Any(property => Carries(property.Name, values) || Carries(property.Value, values)),
            _ => false
        };
    }
}
