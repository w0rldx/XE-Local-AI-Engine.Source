namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Globalization;
using System.Security.Cryptography;

/// <summary>Canonical V1 hash of the complete physical-member closure.</summary>
public static class GgufPhysicalMemberSetHash
{
    public static string ComputeV1(IEnumerable<InstalledModelPhysicalMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        using var buffer = new MemoryStream();
        GgufModelContentFingerprint.WriteField(buffer, "gguf-physical-member-set-v1");
        foreach (var member in members.OrderBy(static member => member.RelativePath, StringComparer.Ordinal)
                                      .ThenBy(static member => member.Role))
        {
            if (member.SizeBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(members), "A physical member size cannot be negative.");
            }

            if (member.Role == InstalledModelPhysicalMemberRole.Sidecar
                    ? member.MemberFingerprint is not null || member.MetadataSchemaVersion is null
                    : !string.Equals(member.MemberFingerprint, GgufMemberFingerprint.Compute(member.Sha256, member.SizeBytes), StringComparison.Ordinal)
                      || member.MetadataSchemaVersion is not null)
            {
                throw new ArgumentException("The physical member fingerprint/schema combination is invalid.", nameof(members));
            }

            var roleToken = member.Role switch
            {
                InstalledModelPhysicalMemberRole.Weight => "weight",
                InstalledModelPhysicalMemberRole.Projector => "projector",
                InstalledModelPhysicalMemberRole.Sidecar => "sidecar",
                _ => throw new ArgumentOutOfRangeException(nameof(members), "The physical member role is invalid.")
            };
            GgufModelContentFingerprint.WriteField(buffer, roleToken);
            GgufModelContentFingerprint.WriteField(buffer, GgufModelContentFingerprint.NormalizeRelativePath(member.RelativePath));
            GgufModelContentFingerprint.WriteField(buffer, member.SizeBytes.ToString(CultureInfo.InvariantCulture));
            GgufMemberFingerprint.ValidateHash(member.Sha256);
            GgufModelContentFingerprint.WriteField(buffer, member.Sha256);
            GgufModelContentFingerprint.WriteField(buffer, member.Required ? "true" : "false");
            GgufModelContentFingerprint.WriteField(buffer,
                member.MetadataSchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "null");
            var aliases = member.OwningAliases.Distinct(StringComparer.Ordinal)
                                .OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(static alias => alias, StringComparer.Ordinal)
                                .ToArray();
            GgufModelContentFingerprint.WriteField(buffer, aliases.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var alias in aliases)
            {
                GgufModelContentFingerprint.WriteField(buffer, alias);
            }
        }

        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
