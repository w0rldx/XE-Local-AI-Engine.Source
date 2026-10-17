namespace XE_Local_AI_Engine.Tests.Testing;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

/// <summary>
///     Serializes a <c>PUT node-settings</c> body the way a partial-update client must: null members are OMITTED.
/// </summary>
/// <remarks>
///     On that endpoint an explicit JSON null clears a member back to its default, so the plain
///     <see cref="JsonContent.Create{T}(T, System.Net.Http.Headers.MediaTypeHeaderValue?, JsonSerializerOptions?)" /> on the
///     typed request would send every unset member as null and clear them all. A test that means "clear" names the
///     members (CLR property names) to send as an explicit null.
/// </remarks>
internal static class NodeSettingsRequestContent
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonContent Create(SaveNodeSettingsRequest request, params string[] clearedMembers)
    {
        var body = JsonSerializer.SerializeToNode(request, Options)!.AsObject();
        foreach (var member in clearedMembers)
        {
            body[JsonNamingPolicy.CamelCase.ConvertName(member)] = null;
        }

        return JsonContent.Create(body);
    }
}
