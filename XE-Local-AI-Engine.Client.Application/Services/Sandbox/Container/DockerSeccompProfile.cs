namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Reflection;
using System.Text.Json;

/// <summary>
///     The engine-owned seccomp profile every sandbox container is created under, and the <c>security-opt</c> string that carries it.
/// </summary>
/// <remarks>
///     PROVENANCE: <c>seccomp-default.json</c> is Docker's own default profile, copied verbatim from <c>moby/profiles</c> tag
///     <c>seccomp/v0.2.3</c>, commit <c>836ae4d37ef2ec995c77c99fc55f5b5f3af3a897</c>, SHA-256
///     <c>536529b665dd0972c37bfb569f5d4ac8a53592e7b00752bc39ff063ca9864c74</c> — the split-out repository, not <c>moby/moby</c>, whose
///     own path 404s on current tags. A copy ships because "applied by default" is not VERIFIABLE: no <c>seccomp=</c> option reads back
///     <c>SecurityOpt: null</c>, as a seccomp-disabled daemon does. Vendoring evidence and API shape: wiki 12 section 7.4.
/// </remarks>
internal static class DockerSeccompProfile
{
    /// <summary>The <c>security-opt</c> key the daemon renders a seccomp profile under, in both directions.</summary>
    internal const string OptionPrefix = "seccomp=";

    /// <summary>The value that means "no profile". Read back verbatim from a container created with it.</summary>
    internal const string Unconfined = "unconfined";

    private const string ResourceNameSuffix = "Services.Sandbox.Container.seccomp-default.json";

    // Loaded once. The profile is ~13 KB on disk and ~9 KB compacted, and every container create carries it.
    private static readonly Lazy<string> LazyOption = new(BuildOption, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The <c>seccomp=&lt;profile&gt;</c> security option to pass at create time.</summary>
    /// <remarks>
    ///     Throws <see cref="DockerRuntimeException" /> when the embedded asset is missing or unparseable, because a create that silently
    ///     dropped the profile would produce a container the read-back cannot distinguish from an unconfined one.
    /// </remarks>
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

    /// <summary>Reads the embedded profile and returns the option string, compacted.</summary>
    /// <remarks>
    ///     Compacted because that is what the daemon stores and echoes back: the CLI runs the file through <c>json.Compact</c> before
    ///     sending, so a compacted request makes the inspect read-back byte-identical to what was asked for rather than merely equivalent.
    ///     The whitespace is also ~4 KB per create that nothing reads.
    /// </remarks>
    private static string BuildOption()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
                                   .FirstOrDefault(name => name.EndsWith(ResourceNameSuffix, StringComparison.Ordinal))
                           ?? throw new DockerRuntimeException(DockerDaemonPreflightStatus.NotConfigured,
                               $"This build carries no embedded seccomp profile (expected a manifest resource ending in '{ResourceNameSuffix}').");

        // Forced sync: BuildOption is the factory delegate of the process-lifetime Lazy<string> above, a Func<string>
        // by contract, reading a resource embedded in this assembly.
#pragma warning disable MA0045 // forced sync: Lazy<T> factory delegate (see comment above)
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new DockerRuntimeException(DockerDaemonPreflightStatus.NotConfigured,
                               $"The embedded seccomp profile '{resourceName}' could not be opened.");

        try
        {
            using var document = JsonDocument.Parse(stream);
            return OptionPrefix + JsonSerializer.Serialize(document.RootElement);
#pragma warning restore MA0045
        }
        catch (JsonException exception)
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.NotConfigured,
                $"The embedded seccomp profile '{resourceName}' is not valid JSON, so it cannot be sent to the daemon.",
                exception);
        }
    }
}
