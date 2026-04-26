namespace XE_Local_AI_Engine.Testing.FakeOllama
{
    using System.Text.Json.Serialization;

    [JsonSerializable(typeof(FakeOllamaFailureRequest))]
    [JsonSerializable(typeof(FakeOllamaScriptRequest))]
    [JsonSerializable(typeof(FakeOllamaRequest))]
    [JsonSerializable(typeof(FakeOllamaRequest[]))]
    [JsonSerializable(typeof(IReadOnlyList<FakeOllamaRequest>))]
    internal sealed partial class FakeOllamaJsonContext : JsonSerializerContext
    {
    }

    internal sealed record FakeOllamaFailureRequest(string Failure);

    internal sealed record FakeOllamaScriptRequest(IReadOnlyList<string> Tokens);
}
