namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Reflection;
using System.Text.Json;

/// <summary>
///     The engine-owned seccomp profile every sandbox container is created under, and the <c>security-opt</c> string
///     that carries it.
///     <para>
///         <b>Provenance.</b> The bytes in <c>seccomp-default.json</c> are Docker's own default profile, copied
///         verbatim from <c>https://github.com/moby/profiles/blob/seccomp/v0.2.3/seccomp/default.json</c> (tag
///         <c>seccomp/v0.2.3</c>, commit <c>836ae4d37ef2ec995c77c99fc55f5b5f3af3a897</c>, SHA-256
///         <c>536529b665dd0972c37bfb569f5d4ac8a53592e7b00752bc39ff063ca9864c74</c>, fetched 2026-08-25). That module
///         is what the daemon itself vendors — <c>moby/moby</c>'s <c>vendor/modules.txt</c> pins
///         <c>github.com/moby/profiles/seccomp v0.2.3</c> — so the profile shipped here is the daemon's builtin, not a
///         hand-written approximation. It moved out of <c>moby/moby</c>'s <c>profiles/seccomp/default.json</c> after
///         v28.0.x; that path 404s on current tags, which is why this cites the split-out repository.
///     </para>
///     <para>
///         <b>Why ship a copy at all, when the daemon already applies this by default?</b> Because "by default" is not
///         verifiable. A container created with no <c>seccomp=</c> option reads back with <c>SecurityOpt: null</c>
///         (measured against Engine 29.7.2), which is the <em>same</em> read-back as a daemon started with seccomp
///         disabled entirely. Asking for the profile explicitly is the only way the fail-closed read-back in
///         <c>DockerSandboxHardening.VerifySecurityOptions</c> can tell a confined container from an unconfined one.
///     </para>
///     <para>
///         <b>The Engine API takes profile CONTENT, not a path.</b> The <c>docker</c> CLI reads the file named by
///         <c>--security-opt seccomp=&lt;path&gt;</c> and sends its JSON; the daemon never opens a host path on the
///         client's behalf. Measured against Engine 29.7.2: a container created with
///         <c>--security-opt seccomp=/tmp/default.json</c> inspects back as <c>seccomp={"defaultAction":…}</c> — the
///         compacted JSON — and never as the path. So there is nothing to materialize on disk for the daemon to read,
///         and this profile stays an embedded resource rather than a file written into the node data directory.
///     </para>
/// </summary>
internal static class DockerSeccompProfile
{
    /// <summary>The <c>security-opt</c> key the daemon renders a seccomp profile under, in both directions.</summary>
    internal const string OptionPrefix = "seccomp=";

    /// <summary>The value that means "no profile". Read back verbatim from a container created with it.</summary>
    internal const string Unconfined = "unconfined";

    private const string ResourceNameSuffix = "Services.Sandbox.Container.seccomp-default.json";

    // Loaded once. The profile is ~13 KB on disk and ~9 KB compacted, and every container create carries it.
    private static readonly Lazy<string> LazyOption = new(BuildOption, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     The <c>seccomp=&lt;profile&gt;</c> security option to pass at create time. Throws
    ///     <see cref="DockerRuntimeException" /> when the embedded asset is missing or unparseable, because a create
    ///     that silently dropped the profile would produce a container the read-back cannot distinguish from an
    ///     unconfined one.
    /// </summary>
    internal static string SecurityOption => LazyOption.Value;

    /// <summary>
    ///     Whether <paramref name="securityOption" /> is a seccomp option that names a real profile — present,
    ///     non-empty, and not <see cref="Unconfined" />.
    /// </summary>
    internal static bool NamesAProfile(string securityOption)
    {
        if (!securityOption.StartsWith(OptionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = securityOption[OptionPrefix.Length..].Trim();
        return value.Length > 0 && !value.Equals(Unconfined, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Reads the embedded profile and returns the option string, compacted.
    ///     <para>
    ///         Compacted because that is what the daemon stores and echoes back: the CLI runs the file through
    ///         <c>json.Compact</c> before sending it, so a compacted request makes the inspect read-back
    ///         byte-identical to what was asked for rather than merely equivalent to it. The whitespace is also ~4 KB
    ///         per create that nothing reads.
    ///     </para>
    /// </summary>
    private static string BuildOption()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
                                   .FirstOrDefault(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal))
                           ?? throw new DockerRuntimeException(DockerDaemonPreflightStatus.NotConfigured,
                               $"This build carries no embedded seccomp profile (expected a manifest resource ending in '{ResourceNameSuffix}').");

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new DockerRuntimeException(DockerDaemonPreflightStatus.NotConfigured,
                               $"The embedded seccomp profile '{resourceName}' could not be opened.");

        try
        {
            using var document = JsonDocument.Parse(stream);
            return OptionPrefix + JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.NotConfigured,
                $"The embedded seccomp profile '{resourceName}' is not valid JSON, so it cannot be sent to the daemon.",
                exception);
        }
    }
}
