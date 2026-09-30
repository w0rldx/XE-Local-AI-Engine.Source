namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;

internal static class WorkSessionToolSerialization
{
    /// <summary>
    ///     Shared by every work-session handler, and non-generic on purpose: one copy of the options rather than one per
    ///     closed generic type.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
