// Grouped rather than one type per file, which is how the sibling Testing.FakeOllama is laid out. These are the
// small value types the scenario API is written in — a stream tag, a frame, a mount point, an exec outcome, a
// pull line, a recorded request, a seeded image — and they are only meaningful as a set: a reader arriving at
// FakeDockerState needs all seven at once, and seven files of a dozen lines each would scatter them. The
// substantial types (the server, the state, the container, the network, the exec session) each have their own file.

namespace XE_Local_AI_Engine.Testing.FakeDocker;

/// <summary>Which half of a demultiplexed Docker stream a frame belongs to. The values are Docker's own frame tags.</summary>
public enum FakeDockerStreamKind
{
    /// <summary>Docker's <c>stdin</c> frame tag. Never written by the fake; it exists because the tag is Docker's.</summary>
    StandardInput = 0,

    /// <summary>Docker's <c>stdout</c> frame tag.</summary>
    StandardOutput = 1,

    /// <summary>Docker's <c>stderr</c> frame tag.</summary>
    StandardError = 2
}

/// <summary>One frame of a container log or exec stream, written with Docker's 8-byte multiplexed header.</summary>
public sealed record FakeDockerLogFrame(FakeDockerStreamKind Stream, string Text);

/// <summary>
///     One entry of a container's <em>effective</em> mount set — the response's top-level <c>Mounts[]</c>, which is
///     where an anonymous volume an image's own <c>VOLUME</c> instruction created shows up. Deliberately distinct
///     from the <c>HostConfig.Mounts</c> the create request carried, which is what was <em>requested</em>.
/// </summary>
public sealed record FakeDockerMountPoint(string Type, string Source, string Destination, bool ReadWrite)
{
    /// <summary>The volume name, set for an anonymous volume and absent for a bind.</summary>
    public string? Name { get; init; }
}

/// <summary>What a scripted <c>exec</c> reports back: its exit code and whatever it wrote on each stream.</summary>
public sealed record FakeDockerExecOutcome(long ExitCode, string StandardOutput = "", string StandardError = "");

/// <summary>
///     One line of a scripted <c>POST /images/create</c> progress stream, in the shape
///     <c>PullProgressAggregator.Fold</c> reads: a line with no <see cref="Id" /> or no <see cref="Status" /> is
///     narration, and a <see cref="Status" /> starting with <c>Pulling from</c> is the opening narration line that
///     carries an id and is still not a layer.
/// </summary>
public sealed record FakeDockerPullLine
{
    /// <summary>The layer id, or null for a narration line.</summary>
    public string? Id { get; init; }

    /// <summary>The daemon's status word — <c>Downloading</c>, <c>Extracting</c>, <c>Pull complete</c>, …</summary>
    public string? Status { get; init; }

    /// <summary>Bytes fetched so far, emitted as <c>progressDetail.current</c>.</summary>
    public long? Current { get; init; }

    /// <summary>Bytes in total, emitted as <c>progressDetail.total</c>. Absent means "the daemon did not say".</summary>
    public long? Total { get; init; }

    /// <summary>
    ///     An <c>errorDetail.message</c>. A pull that fails part-way still completes the HTTP call normally, so this
    ///     is the only way to script the failure the client is supposed to notice.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
///     One request the fake served, recorded so a test can assert on what the client sent rather than only on what
///     it did with the answer — the stop endpoint's <c>t=</c> grace period is the case that matters.
/// </summary>
public sealed record FakeDockerRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query);

/// <summary>
///     An image the fake daemon holds. <see cref="DeclaredVolumes" /> stands in for the image's own <c>VOLUME</c>
///     instructions: the fake synthesizes an anonymous mount into the effective set for each of them at create time,
///     which is how a real daemon reports them.
/// </summary>
public sealed class FakeDockerImage
{
    /// <summary>The full digest-pinned reference, for example <c>busybox@sha256:…</c>.</summary>
    public required string Reference { get; init; }

    /// <summary>Container paths the image declares as volumes, for example <c>/data</c>.</summary>
    public IReadOnlyList<string> DeclaredVolumes { get; init; } = [];
}
