namespace XE_Local_AI_Engine.Cli.Api;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using XE_Local_AI_Engine.Cli.Auth;

/// <summary>The REST endpoints the CLI uses, each with the bearer token attached and one retry after a 401 renewal.</summary>
internal sealed class NodeApiClient
{
    private readonly HttpClient _http;
    private readonly NodeAuthClient _auth;

    /// <param name="http">A client from <see cref="NodeHttp.CreateClient" />; the caller owns it.</param>
    public NodeApiClient(HttpClient http, NodeAuthClient auth)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(auth);
        _http = http;
        _auth = auth;
    }

    public Task<ConversationList> ListConversationsAsync(bool includeArchived, int? limit, CancellationToken cancellationToken)
    {
        var query = string.Create(CultureInfo.InvariantCulture, $"chat/conversations?includeArchived={(includeArchived ? "true" : "false")}");
        if (limit is { } value)
        {
            query += string.Create(CultureInfo.InvariantCulture, $"&limit={value}");
        }

        return SendAsync<ConversationList>(HttpMethod.Get, query, body: null, cancellationToken);
    }

    public Task<Conversation> CreateConversationAsync(CreateConversationRequest request, CancellationToken cancellationToken) =>
        SendAsync<Conversation>(HttpMethod.Post, "chat/conversations", request, cancellationToken);

    public Task<Conversation> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken) =>
        SendAsync<Conversation>(HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"chat/conversations/{conversationId:D}"), body: null, cancellationToken);

    public Task<CancelMessageResponse> CancelAsync(CancelMessageRequest request, CancellationToken cancellationToken) =>
        SendAsync<CancelMessageResponse>(HttpMethod.Post, "chat/cancel", request, cancellationToken);

    public async Task ResolveApprovalAsync(ResolveApprovalRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendWithRenewalAsync(HttpMethod.Post, "chat/approvals/resolve", request, cancellationToken);
        await NodeHttp.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ResolveQuestionAsync(ResolveQuestionRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendWithRenewalAsync(HttpMethod.Post, "chat/questions/resolve", request, cancellationToken);
        await NodeHttp.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<LocalModelList> ListModelsAsync(CancellationToken cancellationToken) =>
        SendAsync<LocalModelList>(HttpMethod.Get, "models", body: null, cancellationToken);

    public Task<RunningModelList> ListRunningModelsAsync(CancellationToken cancellationToken) =>
        SendAsync<RunningModelList>(HttpMethod.Get, "models/running", body: null, cancellationToken);

    public Task<AgentDefinitionList> ListAgentsAsync(CancellationToken cancellationToken) =>
        SendAsync<AgentDefinitionList>(HttpMethod.Get, "agents", body: null, cancellationToken);

    public Task<NodeInfo> GetNodeInfoAsync(CancellationToken cancellationToken) =>
        SendAsync<NodeInfo>(HttpMethod.Get, "diagnostics/node-info", body: null, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendWithRenewalAsync(method, path, body, cancellationToken);
        return await NodeHttp.ReadJsonAsync<T>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRenewalAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await _auth.GetAccessTokenAsync(cancellationToken);
        var response = await SendOnceAsync(method, path, body, token, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        token = await _auth.RenewAccessTokenAsync(token, cancellationToken);
        return await SendOnceAsync(method, path, body, token, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), mediaType: null, NodeHttp.Json);
        }

        return await _http.SendAsync(request, cancellationToken);
    }
}
