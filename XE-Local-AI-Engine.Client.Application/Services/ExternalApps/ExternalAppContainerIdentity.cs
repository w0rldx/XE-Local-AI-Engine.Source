namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using System.Globalization;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Resolves the identity the built-ins <c>XE_UID</c> and <c>XE_GID</c> carry into an application container.
///     <para>
///         This is NOT a <c>--user</c>. The engine passes no user at all: the curated images start as in-container
///         root and drop privileges through their own <c>PUID</c>/<c>PGID</c> entrypoints, and forcing a uid breaks
///         that and breaks a port-80 bind. What the built-ins do is tell such an image which identity to chown its
///         data to, so that the identity it ends up running as can actually write the engine-created bind mount.
///     </para>
///     <para>
///         Development Mode resolves the same question for its own containers and this deliberately does not call it.
///         That resolver reads Development Mode's <c>ContainerSandbox</c> options, which would silently outrank
///         <c>ExternalApps:ContainerIdentity</c>, and it throws for uid 0 on a rootful daemon — a refusal that belongs
///         to a provider which really does pass <c>--user</c>, not to two environment values.
///     </para>
/// </summary>
public static class ExternalAppContainerIdentity
{
    /// <summary>
    ///     The in-container id a rootless daemon maps to the invoking user, i.e. to the daemon owner's own host
    ///     account. Zero because of that mapping, not because root is wanted: under a rootless daemon in-container
    ///     root is an unprivileged host account, and it is the only identity that can write a bind mount the engine
    ///     created.
    /// </summary>
    private const int RootlessMappedId = 0;

    /// <summary>
    ///     On Windows and macOS the engine is a native host process while the container is Linux, so the host's own
    ///     account identifiers name nothing inside it. 1000 is the conventional first non-root Linux account, and an
    ///     operator whose image expects another id sets <c>ExternalApps:ContainerIdentity</c>.
    /// </summary>
    internal const int DesktopDefaultId = 1000;

    /// <summary>
    ///     Resolves the identity for a daemon that either does or does not report itself rootless.
    ///     <paramref name="containerIdentityOption" /> is <c>ExternalApps:ContainerIdentity</c> and wins on every
    ///     daemon; the options validator has already refused anything that is not <c>uid:gid</c>.
    /// </summary>
    public static ResolvedContainerIdentity Resolve(bool daemonIsRootless, string? containerIdentityOption)
    {
        return Resolve(daemonIsRootless, containerIdentityOption, ReadHostUserId, ReadHostGroupId);
    }

    /// <summary>
    ///     The engine's own effective user id on Linux, and the conventional first non-root Linux account everywhere
    ///     else — on Windows and macOS the engine is a native host process and its account names nothing inside a
    ///     Linux container.
    /// </summary>
    internal static int ReadHostUserId()
    {
        return OperatingSystem.IsLinux() ? (int)GetEffectiveUserId() : DesktopDefaultId;
    }

    /// <summary>The group half of <see cref="ReadHostUserId" />, under the same rule.</summary>
    internal static int ReadHostGroupId()
    {
        return OperatingSystem.IsLinux() ? (int)GetEffectiveGroupId() : DesktopDefaultId;
    }

    /// <summary>
    ///     The resolution as a pure function of its inputs, so the rules for daemons and platforms this machine is not
    ///     can be tested rather than reasoned about.
    /// </summary>
    internal static ResolvedContainerIdentity Resolve(bool daemonIsRootless,
        string? containerIdentityOption,
        Func<int> userIdReader,
        Func<int> groupIdReader)
    {
        ArgumentNullException.ThrowIfNull(userIdReader);
        ArgumentNullException.ThrowIfNull(groupIdReader);

        if (TryParseOption(containerIdentityOption, out var configured))
        {
            return configured;
        }

        return daemonIsRootless
            ? new ResolvedContainerIdentity { UserId = RootlessMappedId, GroupId = RootlessMappedId }
            : new ResolvedContainerIdentity { UserId = userIdReader(), GroupId = groupIdReader() };
    }

    private static bool TryParseOption(string? option, out ResolvedContainerIdentity identity)
    {
        identity = new ResolvedContainerIdentity { UserId = 0, GroupId = 0 };
        if (string.IsNullOrWhiteSpace(option))
        {
            return false;
        }

        var separator = option.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || !int.TryParse(option.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            || !int.TryParse(option.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var groupId))
        {
            throw new ArgumentException($"'{option}' is not a 'uid:gid' container identity.", nameof(option));
        }

        identity = new ResolvedContainerIdentity { UserId = userId, GroupId = groupId };
        return true;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getegid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveGroupId();
}
