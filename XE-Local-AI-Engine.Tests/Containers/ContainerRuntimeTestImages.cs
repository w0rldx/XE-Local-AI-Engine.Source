namespace XE_Local_AI_Engine.Tests.Containers;

/// <summary>
///     The container images the application-container tests run against.
///     <para>
///         One is pulled and pinned by digest; the other is built in-daemon by
///         <see cref="VolumeDeclaringImageFixture" />. Nothing is pinned by tag: the runtime refuses a reference
///         that is not digest-pinned, and a tag would let a different image answer to the same name.
///     </para>
/// </summary>
public static class ContainerRuntimeTestImages
{
    /// <summary>
    ///     BusyBox, the general-purpose fixture image. Resolved 2026-09-11 from the tag <c>busybox:1.37</c> with
    ///     <c>docker buildx imagetools inspect busybox:1.37</c>; 2.2 MB, declares no <c>VOLUME</c>.
    ///     <para>
    ///         Chosen over Alpine — which the Development Mode sandbox suite uses — because <c>httpd</c> and
    ///         <c>wget</c> are core applets here, so a container can serve and another can fetch without a package
    ///         install the tests would then depend on a network for.
    ///     </para>
    ///     <para>
    ///         It stays a genuinely pulled image, and is the only one: the pull-progress test needs a real
    ///         per-layer registry pull to have layers to count. Nothing pre-pulls it — the suite that uses it is
    ///         opt-in and pulls through the production path on first use.
    ///     </para>
    /// </summary>
    public const string Busybox = "busybox@sha256:9db7b59979c38555a39def84a31fb98b5296952f9e3afd4f6f11f05b07adfab0";

    /// <summary>
    ///     The container path <see cref="VolumeDeclaringImageFixture" />'s Dockerfile declares as a volume. A
    ///     constant, unlike that image's reference, because it is a property of the two lines that fixture writes
    ///     rather than of anything a registry serves.
    /// </summary>
    public const string VolumeDeclaringImagePath = "/data";
}
