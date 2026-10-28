namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Text;

/// <summary>Keeps the last request body and answers with a minimal chat completion, so a test can read what reached the wire.</summary>
internal sealed class ChatCompletionCapturingHandler : HttpMessageHandler
{
    private const string CannedCompletion =
        "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"test-model\","
        + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],"
        + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    public string? CapturedBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CannedCompletion, Encoding.UTF8, "application/json")
        };
    }
}
