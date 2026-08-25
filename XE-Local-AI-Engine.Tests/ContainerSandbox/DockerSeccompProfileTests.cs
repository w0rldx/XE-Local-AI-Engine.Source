namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the embedded seccomp profile to the upstream bytes its provenance header names
///     (moby/profiles tag <c>seccomp/v0.2.3</c>). An accidental edit to the JSON would otherwise ship silently.
/// </summary>
public sealed class DockerSeccompProfileTests
{
    private const string UpstreamSha256 = "536529b665dd0972c37bfb569f5d4ac8a53592e7b00752bc39ff063ca9864c74";

    [Test]
    public void EmbeddedProfile_MatchesTheUpstreamBytesTheProvenanceHeaderNames()
    {
        var assembly = typeof(DockerSeccompProfile).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("seccomp-default.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));

        AssertEx.Equal(UpstreamSha256, hash);
    }

    [Test]
    public void SecurityOption_CarriesTheProfileContentNotAPath()
    {
        AssertEx.True(DockerSeccompProfile.SecurityOption.StartsWith("seccomp={", StringComparison.Ordinal),
            DockerSeccompProfile.SecurityOption[..Math.Min(40, DockerSeccompProfile.SecurityOption.Length)]);
    }
}
