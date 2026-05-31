namespace XE_Local_AI_Engine.Client.Testing;

using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

/// <summary>
///     Represents http forwarding outbound event recorder.
/// </summary>
public sealed class HttpForwardingOutboundEventRecorder : IOutboundEventRecorder, IDisposable
{
    private const string SinkTokenHeader = "X-Test-Sink-Token";

    private readonly HttpClient _httpClient;
    private readonly string _sinkToken;

    public HttpForwardingOutboundEventRecorder(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var baseUrl = configuration.GetValue<string>("Pipeline:SinkBaseUrl");
        _sinkToken = configuration.GetValue<string>("Pipeline:SinkToken")
                     ?? throw new InvalidOperationException("Pipeline:SinkToken is required when outbound event recording is enabled.");

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Pipeline:SinkBaseUrl is required when outbound event recording is enabled.");
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task RecordAsync(string method, object? payload, long sequenceNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/test/events")
        {
            Content = JsonContent.Create(new
            {
                source = "AiEngineOutbound",
                method,
                payload,
                sequenceNumber,
                capturedAtUtc = DateTimeOffset.UtcNow
            })
        };
        request.Headers.TryAddWithoutValidation(SinkTokenHeader, _sinkToken);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
