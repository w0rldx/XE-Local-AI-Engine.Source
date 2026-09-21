namespace XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

/// <summary>
///     Minimal construction helper that builds the Codex inner <see cref="IChatClient" /> against the pinned
///     OpenAI 2.10.0 / Microsoft.Extensions.AI.OpenAI 10.6.0 surface, performing no I/O.
/// </summary>
/// <remarks>
///     API facts reflection-confirmed against the pinned assemblies: <c>OpenAIClientOptions.Endpoint</c> is a settable
///     <see cref="Uri" />; its <c>Transport</c> takes an <see cref="HttpClient" /> through the plain
///     <see cref="HttpClientPipelineTransport" /> constructor, which keeps SDK transport logging OFF, rather than the
///     logging overload; and <c>GetResponsesClient()</c> is parameterless, the model id going to
///     <c>ResponsesClient.AsIChatClient(modelId)</c> — the Responses adapter, NOT the ChatClient adapter.
/// </remarks>
internal static class CodexChatClientConstruction
{
    /// <summary>
    ///     The dummy API key exists only to satisfy the SDK ctor; <c>CodexAuthHandler</c> strips or replaces the
    ///     resulting <c>Authorization</c>, so "unused" never reaches the wire.
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
            Transport = new HttpClientPipelineTransport(httpClient)
        };

        var openAiClient = new OpenAIClient(new ApiKeyCredential(DummyApiKey), options);
        var responsesClient = openAiClient.GetResponsesClient();
        return responsesClient.AsIChatClient(modelId);
    }
}
