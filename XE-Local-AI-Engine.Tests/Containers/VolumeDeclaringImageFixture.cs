namespace XE_Local_AI_Engine.Tests.Containers;

using System.Formats.Tar;
using System.Globalization;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using TUnit.Core.Exceptions;

/// <summary>
///     An image whose own Dockerfile declares a <c>VOLUME</c>, built in the daemon and removed again.
///     <para>
///         It used to be a pinned <c>redis</c> digest, which made a 16 MB registry pull a hard dependency of the
///         suite for the sake of two lines of Dockerfile. Building it from the BusyBox base the suite already
///         needs costs no network at all, and the resulting anonymous mount is the same one: the daemon
///         materialises a volume for every <c>VOLUME</c> instruction regardless of where the image came from.
///     </para>
///     <para>
///         The reference is <b>computed every run, never hardcoded</b>. A local build's <c>RepoDigests</c> entry
///         is a manifest digest over the base image, the Dockerfile bytes and the builder's own output format, so
///         a base bump, a one-byte edit or a daemon upgrade all move it. A test carrying a copy of it would fail
///         for a reason that has nothing to do with what it asserts.
///     </para>
/// </summary>
public sealed class VolumeDeclaringImageFixture : IAsyncDisposable
{
    /// <summary>Marks the image as this suite's, so one left behind by a killed run is identifiable.</summary>
    private const string FixtureLabel = "xe.test.fixture";

    private const string FixtureLabelValue = "container-runtime-volume-declaring";

    private readonly DockerClient _client;

    private VolumeDeclaringImageFixture(DockerClient client, string tag, string reference)
    {
        _client = client;
        Tag = tag;
        Reference = reference;
    }

    /// <summary>The digest-pinned reference the runtime under test will accept, resolved from this build.</summary>
    public string Reference { get; }

    /// <summary>The local tag the build was given, which is what identifies the image for removal.</summary>
    public string Tag { get; }

    /// <summary>
    ///     Build the image and resolve its digest-pinned reference.
    /// </summary>
    /// <param name="client">
    ///     A raw Docker client, whose OWNERSHIP transfers to the fixture: disposing the fixture disposes it.
    ///     Deliberately not <c>IContainerRuntime</c>: the product never builds an image, so the runtime contract
    ///     has no build member and must not grow one for a test's convenience.
    /// </param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public static async Task<VolumeDeclaringImageFixture> BuildAsync(DockerClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        // A repository name unique to this run, so two checkouts building the same two lines at the same time
        // cannot remove each other's image in cleanup.
        var tag = string.Create(CultureInfo.InvariantCulture,
            $"xe-test-volfixture-{Guid.NewGuid().ToString("N")[..8]}:local");

        using var context = BuildContext();

        // The parameters-first overload, which WAITS for the build. The (contents, parameters) one is [Obsolete]
        // precisely because it returns before the build finishes, and an inspect straight after it would race.
        await client.Images
                    .BuildImageFromDockerfileAsync(new ImageBuildParameters
                        {
                            Tags = [tag],
                            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                [FixtureLabel] = FixtureLabelValue
                            },
                            // Nothing to fetch: the base is already local, and asking the daemon to check the
                            // registry would put a network round trip back into a test that exists to remove one.
                            Pull = "0",
                            Remove = true,
                            ForceRemove = true
                        },
                        context,
                        authConfigs: null,
                        headers: null,
                        new Progress<JSONMessage>(),
                        cancellationToken)
                    .ConfigureAwait(false);

        var inspected = await client.Images.InspectImageAsync(tag, cancellationToken).ConfigureAwait(false);
        var reference = inspected.RepoDigests?.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(reference))
        {
            await RemoveAsync(client, tag).ConfigureAwait(false);
            throw new SkipTestException(NoDigestReason(tag));
        }

        return new VolumeDeclaringImageFixture(client, tag, reference);
    }

    public async ValueTask DisposeAsync()
    {
        await RemoveAsync(_client, Tag).ConfigureAwait(false);
        _client.Dispose();
    }

    /// <summary>
    ///     Why an image store that records no <c>RepoDigests</c> for a local build cannot make this assertion,
    ///     and why the image id is not a way around it.
    ///     <para>
    ///         The daemon resolves a <c>name@sha256:…</c> reference THROUGH <c>RepoDigests</c>, not by content:
    ///         measured on Docker Engine 29.8.0 by creating from a repository name that holds the right digest
    ///         (accepted) and from an unrelated name holding the same digest (refused). So on a store that
    ///         records none — the classic overlay2 graphdriver never does for an image that was neither pulled
    ///         nor pushed — there is no <c>@sha256:</c> reference at all. The image ID does resolve, but it is a
    ///         bare <c>sha256:…</c> with no <c>@</c>, which is exactly what <c>RunContainerAsync</c>'s
    ///         digest-pin guard refuses, and that guard is product behaviour this fixture will not bend.
    ///     </para>
    ///     <para>
    ///         So the assertion is genuinely unmakeable there rather than merely inconvenient, and the reason
    ///         carries the phrase <c>scripts/run-docker-smoke-local.sh</c> recognises, so such a box reports it as
    ///         an assertion it cannot make rather than as a failed gate.
    ///     </para>
    /// </summary>
    private static string NoDigestReason(string tag)
    {
        return $"SKIPPED — this daemon's image store recorded no RepoDigests for the locally built image '{tag}', and a "
               + "name@sha256: reference resolves through RepoDigests rather than by content, so there is no digest-pinned "
               + "reference for the runtime to accept. The image id resolves but carries no '@', which the digest-pin guard "
               + "refuses by design. An image store that records a digest for a local build (the containerd snapshotter "
               + "does) can make this assertion; no counterpart exists for this host.";
    }

    /// <summary>
    ///     A tar stream holding one <c>Dockerfile</c>: the BusyBox base the suite already has, plus the single
    ///     <c>VOLUME</c> instruction the assertion is about.
    /// </summary>
    private static MemoryStream BuildContext()
    {
        var dockerfile = Encoding.UTF8.GetBytes($"FROM {ContainerRuntimeTestImages.Busybox}\n"
                                                + $"VOLUME {ContainerRuntimeTestImages.VolumeDeclaringImagePath}\n");

        var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        using (var content = new MemoryStream(dockerfile))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = content
            };

            writer.WriteEntry(entry);
        }

        archive.Position = 0;
        return archive;
    }

    private static async Task RemoveAsync(DockerClient client, string tag)
    {
        try
        {
            await client.Images
                        .DeleteImageAsync(tag,
                            new ImageDeleteParameters
                            {
                                Force = true
                            })
                        .ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
            // Best-effort teardown of a local-only image. Cleanup must never replace a test's verdict, and a
            // leftover carries the fixture label so it is identifiable.
        }
    }
}
