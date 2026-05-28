namespace XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Models.Encrypted;

public static class RuntimePackageHistoryHash
{
    public static string Compute(IReadOnlyList<EncryptedConversationMessageDto> conversationContext)
    {
        ArgumentNullException.ThrowIfNull(conversationContext);

        var orderedEntries = conversationContext
                             .OrderBy(static entry => entry.SortOrder)
                             .ThenBy(static entry => entry.Id.ToString("D"), StringComparer.Ordinal)
                             .ToList();

        return ComputeCanonical(orderedEntries);
    }

    public static string BuildExpectedAad(Guid conversationId, Guid messageId, int epochVersion)
    {
        return $"message|{conversationId:D}|{messageId:D}|{epochVersion}";
    }

    private static string ComputeCanonical(List<EncryptedConversationMessageDto> orderedEntries)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(4096);
        using var writer = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            SkipValidation = false
        });

        writer.WriteStartArray();
        foreach (var entry in orderedEntries)
        {
            WriteEntry(writer, entry);
        }

        writer.WriteEndArray();
        writer.Flush();

        return FormatLowercaseHex(SHA256.HashData(bufferWriter.WrittenSpan));
    }

    private static void WriteEntry(Utf8JsonWriter writer, EncryptedConversationMessageDto entry)
    {
        var ciphertextB64 = Convert.ToBase64String(entry.Ciphertext.Span);
        var contentIvB64 = Convert.ToBase64String(entry.ContentIv.Span);
        var aad = entry.Aad;

        RejectUnpairedSurrogates(ciphertextB64, "ciphertext");
        RejectUnpairedSurrogates(contentIvB64, "contentIv");
        RejectUnpairedSurrogates(aad, "aad");

        writer.WriteStartObject();
        writer.WriteString("id"u8, entry.Id.ToString("D"));
        writer.WriteNumber("sortOrder"u8, entry.SortOrder);
        writer.WriteString("role"u8, RoleToString(entry.Role));
        writer.WriteNumber("epochVersion"u8, entry.EpochVersion);
        writer.WriteString("ciphertext"u8, ciphertextB64);
        writer.WriteString("contentIv"u8, contentIvB64);
        writer.WriteString("aad"u8, aad);
        writer.WriteEndObject();
    }

    private static string RoleToString(MessageRole role)
    {
        return role switch
        {
            MessageRole.User => "user",
            MessageRole.Assistant => "assistant",
            MessageRole.System => "system",
            MessageRole.Tool => "tool",
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown MessageRole value.")
        };
    }

    private static void RejectUnpairedSurrogates(string value, string fieldName)
    {
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    throw new ArgumentException($"Field '{fieldName}' contains an unpaired high surrogate at index {i}.");
                }

                i += 2;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new ArgumentException($"Field '{fieldName}' contains an unpaired low surrogate at index {i}.");
            }
            else
            {
                i++;
            }
        }
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
