namespace XE_Local_AI_Engine.Providers.CodexOAuth;

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

/// <summary>
/// Phase 1.6 compile spike (plan §8 P1.6): proves the §5 construction sketch compiles against the pinned
/// OpenAI 2.10.0 / Microsoft.Extensions.AI.OpenAI 10.6.0 surface, before Phase 3 builds the real factory.
///
/// <para>Verified API facts (reflection-confirmed against the pinned assemblies):</para>
/// <list type="bullet">
///   <item><c>OpenAIClientOptions.Endpoint</c> is a settable <see cref="Uri"/>.</item>
///   <item><c>OpenAIClientOptions.Transport</c> is a <see cref="PipelineTransport"/>; wrap an
///   <see cref="HttpClient"/> via <see cref="HttpClientPipelineTransport"/>. The plain
///   <c>HttpClientPipelineTransport(HttpClient)</c> ctor keeps SDK transport logging OFF (M5) — the
///   logging overload <c>(HttpClient, bool, ILoggerFactory)</c> is NOT used.</item>
///   <item><c>OpenAIClient.GetResponsesClient()</c> is parameterless; the model id is passed to
///   <c>ResponsesClient.AsIChatClient(modelId)</c> (the Responses adapter — NOT the ChatClient adapter).</item>
/// </list>
///
/// This builds an <see cref="IChatClient"/> but performs no I/O. The real factory (Phase 3) owns a single
/// shared <see cref="HttpClient"/> whose handler chain includes <c>CodexAuthHandler</c>, forces store=false
/// via <see cref="CodexResponseStoreDisabling"/>, and must NOT dispose the shared client (M3).
/// </summary>
internal static class CodexChatClientConstruction
{
    /// <summary>
    /// The dummy API key exists only to satisfy the SDK ctor; <c>CodexAuthHandler</c> strips/replaces the
    /// resulting <c>Authorization</c> so "unused" never reaches the wire (MF5).
    /// </summary>
    internal const string DummyApiKey = "unused";

    internal static IChatClient Build(Uri codexBaseUri, HttpClient httpClient, string modelId)
    {
        ArgumentNullException.ThrowIfNull(codexBaseUri);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var options = new OpenAIClientOptions
        {
            Endpoint = codexBaseUri,
            Transport = new HttpClientPipelineTransport(httpClient),
        };

        var openAiClient = new OpenAIClient(new ApiKeyCredential(DummyApiKey), options);
        var responsesClient = openAiClient.GetResponsesClient();
        return responsesClient.AsIChatClient(modelId);
    }
}
