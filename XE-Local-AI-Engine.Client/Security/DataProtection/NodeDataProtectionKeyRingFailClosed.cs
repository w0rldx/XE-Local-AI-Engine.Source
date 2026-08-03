namespace XE_Local_AI_Engine.Client.Security.DataProtection;

using Microsoft.AspNetCore.DataProtection.KeyManagement.Internal;

/// <summary>
///     Wires <see cref="NodeDataProtectionKeyRingFailClosedKeyResolver" /> over Data Protection's own default resolver.
///     <para>
///         Extracted from <c>ConfigureServices</c> so the OS decision is a PARAMETER rather than a branch that only a
///         Windows host can reach. That is not tidying: the defect this exists to close was that the decoration
///         happened on the non-Windows branch only, which is exactly the class of mistake an inline
///         <c>OperatingSystem.IsWindows()</c> hides from every test on a Linux machine.
///     </para>
/// </summary>
public static class NodeDataProtectionKeyRingFailClosed
{
    /// <summary>
    ///     The decorator appropriate to the at-rest scheme that host uses: DPAPI on Windows, the operator-secret AES-GCM
    ///     wrapper (BE-02) everywhere else. They differ only in which failure counts as a ring failure and in what the
    ///     operator can do about it — never in whether the ring is allowed to regenerate itself silently.
    /// </summary>
    public static Func<IDefaultKeyResolver, IDefaultKeyResolver> ResolverFactoryFor(bool isWindows)
    {
        return isWindows
            ? NodeDataProtectionKeyRingFailClosedKeyResolver.ForDpapiRing
            : static inner => new NodeDataProtectionKeyRingFailClosedKeyResolver(inner);
    }

    /// <summary>
    ///     Replaces the registered <see cref="IDefaultKeyResolver" /> with the decorated one.
    ///     <para>
    ///         Skipped — leaving the framework's pre-existing behaviour in place — if Data Protection ever stops
    ///         registering the resolver by implementation type, since there would then be no inner instance to
    ///         construct. A fail-closed change on a startup path must never be able to brick a correct install, and
    ///         this is the guard that keeps that true.
    ///     </para>
    /// </summary>
    /// <returns><see langword="true" /> when the decoration was applied.</returns>
    public static bool Decorate(IServiceCollection services, Func<IDefaultKeyResolver, IDefaultKeyResolver> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        var registration = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IDefaultKeyResolver));
        if (registration?.ImplementationType is not { } innerResolverType)
        {
            return false;
        }

        _ = services.Remove(registration);
        _ = services.AddSingleton(serviceProvider =>
            factory((IDefaultKeyResolver)ActivatorUtilities.CreateInstance(serviceProvider, innerResolverType)));
        return true;
    }
}
