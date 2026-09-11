namespace XE_Local_AI_Engine.Tests.Containers;

/// <summary>
///     The container images the application-container tests run against, pinned by digest.
///     <para>
///         Public, and shared on purpose: a later slice that invented its own "busybox-class" image would have no
///         digest and no CI pre-pull step behind it, which is how a suite starts depending on whatever the registry
///         serves today. Both digests are pre-pulled by <c>.github/workflows/build-and-test.yml</c>, so a registry
///         blip fails that step as the infrastructure problem it is rather than as twenty red tests.
///     </para>
///     <para>
///         Nothing is built in-test and no tag appears anywhere: the runtime refuses an image reference that is not
///         digest-pinned, and a tag would let a different image answer to the same name.
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
    /// </summary>
    public const string Busybox = "busybox@sha256:9db7b59979c38555a39def84a31fb98b5296952f9e3afd4f6f11f05b07adfab0";

    /// <summary>
    ///     A pinned image whose own Dockerfile declares a <c>VOLUME</c>, so a container created from it reports an
    ///     anonymous mount nobody asked for. That mount is exactly what a later slice must reject, and only a real
    ///     image can produce it.
    ///     <para>
    ///         Resolved 2026-09-11 from the tag <c>redis:7.4-alpine</c> with
    ///         <c>docker buildx imagetools inspect redis:7.4-alpine</c>; 16 MB. Verified with
    ///         <c>docker image inspect --format '{{json .Config.Volumes}}'</c> on the pulled digest, which reported
    ///         <c>{"/data":{}}</c> — so <see cref="VolumeDeclaringImagePath" /> is the path the daemon will mount.
    ///     </para>
    /// </summary>
    public const string VolumeDeclaringImage = "redis@sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf";

    /// <summary>The container path <see cref="VolumeDeclaringImage" /> declares as a volume.</summary>
    public const string VolumeDeclaringImagePath = "/data";
}
