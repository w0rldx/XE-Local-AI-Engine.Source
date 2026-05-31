namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

using System.Text.RegularExpressions;

/// <summary>
///     Value object carrying docker image reference data.
/// </summary>
public sealed partial record DockerImageReference
{
    private DockerImageReference(string repository, string tag, string digest)
    {
        Repository = repository;
        Tag = tag;
        Digest = digest;
    }

    public string Repository { get; }

    public string Tag { get; }

    public string Digest { get; }

    public string RepositoryWithTag => $"{Repository}:{Tag}";

    public string CanonicalReference => $"{RepositoryWithTag}@sha256:{Digest}";

    public static DockerImageReference Parse(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var match = CanonicalImageRegex().Match(imageReference);
        if (!match.Success)
        {
            throw new FormatException("Image reference must be canonical repo:tag@sha256:<64-hex-digest> and must not use :latest.");
        }

        var tag = match.Groups["tag"].Value;
        if (string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Image reference must not use the latest tag.");
        }

        return new DockerImageReference(match.Groups["repository"].Value,
            tag,
            match.Groups["digest"].Value);
    }

    [GeneratedRegex("^(?<repository>.+):(?<tag>[^:@]+)@sha256:(?<digest>[A-Fa-f0-9]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalImageRegex();
}
