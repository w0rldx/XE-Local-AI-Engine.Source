namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Pins the two things about the converter that are visible outside it: the exact command it runs, and the fact
///     that its failure message carries no filesystem path.
/// </summary>
/// <remarks>
///     No real <c>ffmpeg</c> child is started here. The message sanitizer is the part that matters, because whatever it
///     returns is persisted into the encrypted <c>error_message</c> column and returned to the client verbatim. The two
///     process tests drive a CONTROLLED child through the internal executable seam instead — a script this test wrote —
///     so the cancellation contract can be asserted on a machine with no ffmpeg installed.
/// </remarks>
public sealed class FfmpegAudioTranscoderTests
{
    [Test]
    public void BuildArguments_IsTheExactSixteenKilohertzMonoWavCommand()
    {
        var arguments = FfmpegAudioTranscoder.BuildArguments("/data/tmp/transcription/in.ogg", "/data/tmp/transcription/out.wav");

        AssertEx.Equal("-nostdin -hide_banner -loglevel error -y -i /data/tmp/transcription/in.ogg -ar 16000 -ac 1 -f wav /data/tmp/transcription/out.wav",
            string.Join(' ', arguments),
            "The converter's command is part of the contract: -nostdin keeps a malformed file from parking the child "
            + "on a prompt, and 16 kHz mono WAV is what the runtime decodes natively.");
    }

    [Test]
    public void SanitizeTail_DropsTheEngineTempPathFfmpegEchoes()
    {
        // The real shape: ffmpeg prefixes the failing input with its absolute path.
        const string stderr = "[ogg @ 0x55f] Format ogg detected only with low score\n"
                              + "/home/someone/.local/share/XE-Local-AI-Engine/tmp/transcription/9f2c.ogg: Invalid data found when processing input\n";

        var sanitized = FfmpegAudioTranscoder.SanitizeTail(stderr);

        AssertEx.Equal("Invalid data found when processing input", sanitized);
        AssertEx.False(sanitized.Contains('/', StringComparison.Ordinal) || sanitized.Contains('\\', StringComparison.Ordinal),
            "No path separator may survive into a message that is persisted and returned to the client.");
        AssertEx.False(sanitized.Contains("XE-Local-AI-Engine", StringComparison.OrdinalIgnoreCase),
            "No segment of the node's data directory may survive.");
        AssertEx.False(sanitized.Contains("9f2c", StringComparison.Ordinal),
            "Not even the server-generated file name is worth leaking.");
    }

    [Test]
    [Arguments("C:\\Users\\someone\\AppData\\Local\\XE-Local-AI-Engine\\tmp\\in.m4a: Invalid data found", "A Windows path, whose drive letter carries a colon of its own")]
    [Arguments("Error opening /var/data/tmp/transcription/a.webm and /var/data/tmp/transcription/b.wav", "two paths and no colon separator at all")]
    public void SanitizeTail_LeavesNoPathShapedToken(string stderr, string because)
    {
        var sanitized = FfmpegAudioTranscoder.SanitizeTail(stderr);

        AssertEx.NotEmpty(sanitized, $"A reason must survive for {because}.");
        AssertEx.False(sanitized.Contains('/', StringComparison.Ordinal) || sanitized.Contains('\\', StringComparison.Ordinal),
            $"A path-shaped token survived for {because}: '{sanitized}'.");
    }

    [Test]
    public void SanitizeTail_WhenNothingUsableRemains_SaysSo()
    {
        AssertEx.Equal("the converter reported no reason.", FfmpegAudioTranscoder.SanitizeTail(string.Empty));
        AssertEx.Equal("the converter reported no reason.", FfmpegAudioTranscoder.SanitizeTail("   \n  \n"));

        // A line that is nothing but a path leaves an empty string, which would otherwise be reported as the reason.
        AssertEx.Equal("the converter reported no reason.", FfmpegAudioTranscoder.SanitizeTail("/var/data/tmp/transcription/a.ogg"));
    }

    /// <summary>
    ///     A cancelled conversion kills its child, unwinds with the cancellation, and leaves no process behind.
    /// </summary>
    /// <remarks>
    ///     The only coverage of the real process path — start, cancel, kill, unwind — and the only thing that catches a
    ///     cancellation that hangs on its own pipes instead of returning, which is what the two readers and the bounded
    ///     wait after the kill exist to prevent.
    ///     <para>
    ///         <b>What it cannot isolate.</b> The wait after the kill is what guarantees the child is GONE rather than
    ///         merely signalled, and on Linux that guarantee is unobservable: <see cref="Process.Kill(bool)" /> sends
    ///         SIGKILL and the runtime reaps the child before this method can look, with or without the wait (measured:
    ///         fifteen runs each way, no difference). The failure the wait prevents belongs to Windows, where a handle
    ///         that is still open makes the upload slot's delete fail — and that failure is swallowed by design, so the
    ///         operator's audio survives the request. The assertion below is kept because it is cheap and true, not
    ///         because it distinguishes the two implementations here.
    ///     </para>
    /// </remarks>
    [Test]
    [RunOn(OS.Linux)]
    public async Task ToWav16kMono_WhenCancelled_KillsTheChildAndReportsTheCancellation()
    {
        using var directory = new TempDir();
        var pidPath = Path.Combine(directory.Path, "child.pid");
        var destination = Path.Combine(directory.Path, "out.wav");

        // Ignores the polite signals, writes its pid, claims the destination, and then refuses to finish. The last
        // positional argument is the destination, which is how BuildArguments orders the ffmpeg command line.
        var executable = WriteScript(directory,
            "trap '' TERM INT HUP",
            $"printf '%s' \"$$\" > '{pidPath}'",
            ": > \"${@: -1}\"",
            "sleep 120");

        var transcoder = new FfmpegAudioTranscoder(NullLogger<FfmpegAudioTranscoder>.Instance, executable);
        AssertEx.True(transcoder.IsAvailable, "The internal seam must present the supplied executable as available.");

        using var cancellation = new CancellationTokenSource();
        var running = transcoder.ToWav16kMonoAsync(Path.Combine(directory.Path, "in.ogg"), destination, cancellation.Token);

        await AssertEx.EventuallyAsync(() => ReadPid(pidPath) is not null,
                          TimeSpan.FromSeconds(30),
                          "The controlled converter must start and report its process id.")
                      .ConfigureAwait(false);
        var pid = ReadPid(pidPath) ?? -1;
        AssertEx.NotEqual(-1, pid, "The child's pid is the only handle this test has on it.");

        await cancellation.CancelAsync().ConfigureAwait(false);

        var cancelledAsItMust = false;
        var stillPresentOnReturn = -1;
        try
        {
            // real-timer: a deadline so a wait that never ends reads as a failure instead of hanging the run. A green
            // run returns the moment the conversion unwinds, well inside it.
            _ = await running.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Observed HERE, with nothing between the unwind and the look: from this line on the caller is entitled to
            // delete the destination, so this is the only moment at which the question means anything.
            cancelledAsItMust = true;
            stillPresentOnReturn = Directory.Exists($"/proc/{pid}") ? 1 : 0;
        }

        AssertEx.True(cancelledAsItMust, "A cancelled conversion must report the cancellation.");
        AssertEx.Equal(expected: 0, stillPresentOnReturn,
            $"Process {pid} was still present when the conversion returned: the destination file is about to be "
            + "deleted, and the converter may still be holding it open.");
    }

    /// <summary>The control: the same seam on a child that exits normally still produces the destination file.</summary>
    /// <remarks>
    ///     Without it the cancellation test above would pass against a transcoder that could not convert anything at
    ///     all — a child that is never started is also a child that is gone.
    /// </remarks>
    [Test]
    [RunOn(OS.Linux)]
    public async Task ToWav16kMono_WhenTheConverterExitsCleanly_ReturnsTheDestinationItWrote()
    {
        using var directory = new TempDir();
        var destination = Path.Combine(directory.Path, "out.wav");
        var executable = WriteScript(directory, "printf 'RIFF' > \"${@: -1}\"");

        var transcoder = new FfmpegAudioTranscoder(NullLogger<FfmpegAudioTranscoder>.Instance, executable);

        var produced = await transcoder.ToWav16kMonoAsync(Path.Combine(directory.Path, "in.ogg"), destination, CancellationToken.None)
                                       .ConfigureAwait(false);

        AssertEx.Equal(destination, produced);
        AssertEx.Equal("RIFF", await File.ReadAllTextAsync(destination).ConfigureAwait(false),
            "The converter's own output must reach the destination the caller named.");
    }

    private static int? ReadPid(string pidPath)
    {
        try
        {
            return int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid) ? pid : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The script is mid-write; the caller polls.
            return null;
        }
    }

    private static string WriteScript(TempDir directory, params string[] lines)
    {
        var path = Path.Combine(directory.Path, $"converter-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, string.Join('\n', ["#!/bin/bash", .. lines, string.Empty]));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() =>
            _ = Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xe-ffmpeg-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A scratch directory that outlives the run is litter, never a failure.
            }
        }
    }
}
