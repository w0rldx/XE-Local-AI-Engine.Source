namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Security.Cryptography;

/// <summary>Canonical V1 hash of exact model-name/registry-revision alias pairs.</summary>
public static class GgufRegistryAliasSetHash
{
    public static string ComputeV1(IEnumerable<InstalledModelRegistryAliasSnapshot> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        using var buffer = new MemoryStream();
        GgufModelContentFingerprint.WriteField(buffer, "gguf-registry-alias-set-v1");
        foreach (var alias in aliases.OrderBy(static alias => alias.ModelName, StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(static alias => alias.ModelName, StringComparer.Ordinal))
        {
            GgufModelContentFingerprint.WriteField(buffer, alias.ModelName);
            GgufModelContentFingerprint.WriteField(buffer, alias.RegistryRevision);
        }

        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
