namespace XE_Local_AI_Engine.Cli.Api;

using System.Net.Http.Json;
using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Cli.Discovery;

/// <summary>The one place the CLI builds HTTP transports and reads the node's JSON.</summary>
internal static class NodeHttp
{
    internal const string ApiPrefix = "api/local/v1/";

    /// <summary>The node's wire policy: camelCase, case-insensitive, enums by name.</summary>
    internal static readonly JsonSerializerOptions Json = CreateJsonOptions();

    internal static HttpClient CreateClient(NodeConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

#pragma warning disable CA2000 // The HttpClient owns the handler and disposes it (disposeHandler: true).
        return new HttpClient(options.HandlerFactory?.Invoke() ?? CreateSocketsHandler(options), disposeHandler: true)
        {
            BaseAddress = new Uri(options.Origin, ApiPrefix)
        };
#pragma warning restore CA2000
    }

    /// <summary>
    ///     Copies the node's hub JSON policy onto a SignalR client's options, so enums sent by name bind instead of
    ///     throwing inside the protocol reader.
    /// </summary>
    internal static void ConfigureJson(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(new JsonStringEnumConverter());
    }

    /// <summary>The JSON body of a success answer; any other answer throws <see cref="NodeApiException" />.</summary>
    internal static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
               ?? throw new NodeApiException(response.StatusCode, "The node answered with an empty body.");
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode)
        {
            throw await NodeApiException.FromResponseAsync(response, cancellationToken);
        }
    }

    /// <summary>The certificate rule both transports apply: a valid chain, or the opt-in on a loopback origin.</summary>
    internal static RemoteCertificateValidationCallback? CertificateValidation(NodeConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AcceptAnyServerCertificate)
        {
            return null;
        }

        if (!NodeLocator.IsLoopbackHost(options.Origin.Host))
        {
            throw new InvalidOperationException("Accepting any server certificate is allowed for a loopback origin only.");
        }

        // A self-signed dev certificate fails the chain and may fail the name; a server that sends no certificate still fails.
        return static (_, _, _, errors) =>
            (errors & ~(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)) == SslPolicyErrors.None;
    }

    internal static SocketsHttpHandler CreateSocketsHandler(NodeConnectionOptions options)
    {
        // No cookie jar: a body-mode client must never replay node_rt or an unlock ticket, and redirects are surfaced, not followed.
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false
        };
        if (CertificateValidation(options) is { } callback)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = callback;
        }

        return handler;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ConfigureJson(options);
        return options;
    }
}
