namespace XE_Local_AI_Engine.Installer.Manifest;

using YamlDotNet.Serialization;

/// <summary>
///     Parses the bundle's runtime manifest (<c>managed.yaml</c>) down to the only thing the installer
///     needs for teardown attribution: the declared container names (plan §7.4 inventory). Uses the
///     already-referenced YamlDotNet via an untyped map walk (no DTO classes — the full manifest is the
///     in-distro reconciler's concern, not the installer's).
/// </summary>
public static class InstallerManifestParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public static InstallerManifest Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var root = Deserializer.Deserialize<Dictionary<object, object>>(yaml);
        var names = new List<string>();

        if (root is not null
            && root.TryGetValue("containers", out var containersValue)
            && containersValue is IEnumerable<object> containers)
        {
            foreach (var entry in containers.OfType<IDictionary<object, object>>())
            {
                if (entry.TryGetValue("name", out var nameValue)
                    && nameValue is string name
                    && !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return new InstallerManifest { ContainerNames = names };
    }
}
