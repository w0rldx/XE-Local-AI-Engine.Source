namespace XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Computes the canonical fingerprint of an <see cref="ApplicationManifest" /> — the value carried in
///     <see cref="ApplicationManifest.ManifestSha256" /> and echoed back by install/update commands.
/// </summary>
/// <remarks>
///     The canonical form is fixed because two languages compute it — this type and the Python catalog converter —
///     and is written down rather than inferred: see <c>docs/wiki/23-external-apps.md</c> ("The manifest"). Sorting
///     every object's properties ordinal by name at every level is what makes this record's declaration order and
///     the converter's dict order irrelevant; <c>manifestSha256</c> is removed because a document cannot contain its
///     own hash.
/// </remarks>
public static class ExternalAppManifestFingerprint
{
    /// <summary>The JSON property name the fingerprint excludes from its own input.</summary>
    public const string HashPropertyName = "manifestSha256";

    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Returns the lowercase-hex SHA-256 of <paramref name="manifest" />'s canonical JSON form.</summary>
    public static string Compute(ApplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var serialized = JsonSerializer.SerializeToNode(manifest, CanonicalOptions) as JsonObject
                         ?? new JsonObject();
        _ = serialized.Remove(HashPropertyName);

        var canonicalJson = Canonicalize(serialized)!.ToJsonString(CanonicalOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                {
                    var sorted = new JsonObject();
                    foreach (var property in jsonObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        sorted[property.Key] = Canonicalize(property.Value?.DeepClone());
                    }

                    return sorted;
                }

            case JsonArray jsonArray:
                {
                    var ordered = new JsonArray();
                    foreach (var item in jsonArray)
                    {
                        ordered.Add(Canonicalize(item?.DeepClone()));
                    }

                    return ordered;
                }

            default:
                return node?.DeepClone();
        }
    }
}
