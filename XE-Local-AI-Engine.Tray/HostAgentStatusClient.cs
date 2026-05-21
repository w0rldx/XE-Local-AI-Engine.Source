namespace XE_Local_AI_Engine.Tray;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

internal sealed class HostAgentStatusClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly HostAgentAdminTokenStore _adminTokenStore = new();
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public HostAgentStatusClient()
    {
        _handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        _httpClient = new HttpClient(_handler, false)
        {
            Timeout = RequestTimeout
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _handler.Dispose();
        _disposed = true;
    }

    public async Task<TrayHealthSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var statusUri = TryResolveAdminUri("status");
        if (statusUri is null)
        {
            return TrayHealthSnapshot.Unreachable;
        }

        try
        {
            using var response = await SendAuthorizedAsync(HttpMethod.Get, statusUri, true, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return TrayHealthSnapshot.Unreachable;
            }

            var status = await response.Content.ReadFromJsonAsync<HostAgentStatusDto>(SerializerOptions, cancellationToken).ConfigureAwait(false);
            return TrayHealthSnapshot.FromStatus(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return TrayHealthSnapshot.Unreachable;
        }
        catch (TaskCanceledException)
        {
            return TrayHealthSnapshot.Unreachable;
        }
        catch (JsonException)
        {
            return TrayHealthSnapshot.Unreachable;
        }
        catch (IOException)
        {
            return TrayHealthSnapshot.Unreachable;
        }
        catch (UnauthorizedAccessException)
        {
            return TrayHealthSnapshot.Unreachable;
        }
        catch (InvalidOperationException)
        {
            return TrayHealthSnapshot.Unreachable;
        }
    }

    public async Task<bool> SendLifecycleActionAsync(string endpointName, CancellationToken cancellationToken)
    {
        var actionUri = TryResolveAdminUri(endpointName);
        if (actionUri is null)
        {
            return false;
        }

        var response = await SendAuthorizedAsync(HttpMethod.Post, actionUri, true, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            return response.IsSuccessStatusCode;
        }
    }

    public async Task<IReadOnlyList<string>> ReadDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var logsUri = TryResolveAdminUri("logs?tail=200");
        if (logsUri is null)
        {
            return ["HostAgent admin endpoint is not reachable."];
        }

        var response = await SendAuthorizedAsync(HttpMethod.Get, logsUri, true, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return [$"Unable to load diagnostics: {(int)response.StatusCode} {response.ReasonPhrase}"];
            }

            var tail = await response.Content.ReadFromJsonAsync<HostAgentLogTailDto>(SerializerOptions, cancellationToken).ConfigureAwait(false);
            return tail?.Lines is { Count: > 0 } lines ? lines : ["No diagnostics are available yet."];
        }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method,
        Uri uri,
        bool retryUnauthorized,
        CancellationToken cancellationToken)
    {
        var token = await _adminTokenStore.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAuthorizedOnceAsync(method, uri, token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !retryUnauthorized)
        {
            return response;
        }

        response.Dispose();
        _adminTokenStore.ClearCache();
        token = await _adminTokenStore.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return await SendAuthorizedOnceAsync(method, uri, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAuthorizedOnceAsync(HttpMethod method,
        Uri uri,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static Uri? TryResolveAdminUri(string relativePath)
    {
        var adminBaseUri = TryResolveAdminBaseUri();
        return adminBaseUri is null ? null : new Uri(adminBaseUri, relativePath);
    }

    private static Uri? TryResolveAdminBaseUri()
    {
        var configuredUrl = Environment.GetEnvironmentVariable("XE_HOST_AGENT_ADMIN_URL");
        if (TryCreateLoopbackAdminBaseUri(configuredUrl, out var configuredUri))
        {
            return configuredUri;
        }

        var configuredPort = Environment.GetEnvironmentVariable("XE_HOST_AGENT_ADMIN_PORT");
        if (int.TryParse(configuredPort, out var port) && TryCreateLoopbackAdminBaseUri(port, out var configuredPortUri))
        {
            return configuredPortUri;
        }

        var metadata = HostAgentRuntimeMetadataReader.TryRead();
        return metadata is not null && TryCreateLoopbackAdminBaseUri(metadata.AdminPort, out var metadataUri)
            ? metadataUri
            : null;
    }

    private static bool TryCreateLoopbackAdminBaseUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(parsed.Host, "127.0.0.1", StringComparison.Ordinal))
        {
            return false;
        }

        uri = new Uri($"{parsed.Scheme}://{parsed.Host}:{parsed.Port}/");
        return true;
    }

    private static bool TryCreateLoopbackAdminBaseUri(int port, out Uri? uri)
    {
        uri = null;
        if (port is <= 0 or > 65535)
        {
            return false;
        }

        uri = new Uri($"http://127.0.0.1:{port}/");
        return true;
    }
}

internal sealed record HostAgentLogTailDto(IReadOnlyList<string> Lines);
