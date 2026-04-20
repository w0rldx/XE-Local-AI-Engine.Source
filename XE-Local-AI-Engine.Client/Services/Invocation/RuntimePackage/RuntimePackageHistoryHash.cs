namespace XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models.Encrypted;

public static class RuntimePackageHistoryHash
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Compute(IReadOnlyList<EncryptedConversationMessageDto> conversationContext)
    {
        var canonicalJson = SerializeCanonicalJson(conversationContext);
        return FormatLowercaseHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    public static string SerializeCanonicalJson(IReadOnlyList<EncryptedConversationMessageDto> conversationContext)
    {
        ArgumentNullException.ThrowIfNull(conversationContext);

        var orderedEntries = conversationContext
            .OrderBy(static entry => entry.SortOrder)
            .ThenBy(static entry => entry.Id.ToString("D"), StringComparer.Ordinal)
            .ToList();

        return JsonSerializer.Serialize(orderedEntries, SerializerOptions);
    }

    public static string BuildExpectedAad(Guid conversationId, Guid messageId, int epochVersion)
    {
        return $"message|{conversationId:D}|{messageId:D}|{epochVersion}";
    }

    private static string FormatLowercaseHex(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length * 2, bytes.ToArray(), static (buffer, source) =>
        {
            const string HexAlphabet = "0123456789abcdef";

            for (var index = 0; index < source.Length; index++)
            {
                var value = source[index];
                buffer[index * 2] = HexAlphabet[value >> 4];
                buffer[(index * 2) + 1] = HexAlphabet[value & 0x0F];
            }
        });
    }
}
