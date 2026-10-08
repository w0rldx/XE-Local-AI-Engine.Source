namespace XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FakeOpenAiGatewayFailureRequest))]
[JsonSerializable(typeof(FakeOpenAiGatewayScript))]
[JsonSerializable(typeof(FakeOpenAiGatewayRequest[]))]
internal sealed partial class FakeOpenAiGatewayJsonContext : JsonSerializerContext
{
}
