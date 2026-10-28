namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using System.Text;

/// <summary>Keeps the last request body and answers with a minimal completed Responses object, so a test can read the wire body.</summary>
internal sealed class ResponsesBodyCapturingHandler : HttpMessageHandler
{
    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        const string CannedResponse =
            """{"id":"resp_test","object":"response","status":"completed","model":"gpt-5.4","output":[]}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CannedResponse, Encoding.UTF8, "application/json")
        };
    }
}
