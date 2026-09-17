namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The half of per-application capture only Windows can answer: that the product asks for the target process
///     tree and never its complement, and that the builder behaves the way the implementation depends on.
/// </summary>
/// <remarks>
///     <para>
///         <b>Both class attributes are load-bearing.</b> <c>[RunOn(OS.Windows)]</c> is TUnit's gate and makes these
///         report <i>skipped</i>, with a reason, on a non-Windows host; it is invisible to the CA1416 platform
///         analyzer. <c>[SupportedOSPlatform("windows10.0.19041.0")]</c> is what satisfies that analyzer for
///         <c>WithProcessLoopback</c> — a class-level <c>"windows"</c> does not satisfy a
///         <c>windows10.0.19041.0</c> API. Without the versioned attribute this file does not compile in Release on
///         the Linux gate, and no CA1416 suppression is acceptable in its place: a suppression is exactly how a
///         Windows-only call reaches a Linux host.
///     </para>
///     <para>
///         Nothing here mocks <see cref="WasapiRecorderBuilder" />. It is the thing under test, and mocking it would
///         turn the whole proof into a tautology.
///     </para>
/// </remarks>
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows10.0.19041.0")]
[Category(TestCategories.Unit)]
public sealed class WindowsProcessLoopbackTests
{
    [Test]
    public void CaptureUses_IncludeTargetProcessTree_Never_Exclude()
    {
        AssertEx.True(OperatingSystem.IsWindows(), "[RunOn(OS.Windows)] did not engage.");

        // ExcludeTargetProcessTree captures everything on the endpoint EXCEPT the target and its descendants. Read
        // quickly it looks like "just this application"; it is the opposite, and shipping it would record every
        // other application on the box while the interface claimed one. The constant is the whole product surface
        // for this choice, so asserting it is asserting that the inversion cannot return.
        AssertEx.Equal(ProcessLoopbackMode.IncludeTargetProcessTree, WindowsProcessAudioCaptureSource.CaptureMode,
            "Per-application capture must ask for the target process tree.");
        AssertEx.NotEqual(ProcessLoopbackMode.ExcludeTargetProcessTree, WindowsProcessAudioCaptureSource.CaptureMode,
            "ExcludeTargetProcessTree records every OTHER application; it must never reach the product.");
    }

    [Test]
    public async Task WithProcessLoopback_BuildAsyncSucceeds()
    {
        AssertEx.True(OperatingSystem.IsWindows(), "[RunOn(OS.Windows)] did not engage.");

        // Against this test process's own id: activation is what is under test, not what it hears. Never started,
        // so no audio is captured and no device is held.
        var builder = new WasapiRecorderBuilder()
                      .WithProcessLoopback((uint)Environment.ProcessId, WindowsProcessAudioCaptureSource.CaptureMode)
                      .WithBufferLength(100);

        var recorder = await builder.BuildAsync();
        await using (recorder)
        {
            AssertEx.NotNull(recorder.WaveFormat,
                "The process-loopback device reports a capture format; the converter is constructed from it.");
        }
    }

    [Test]
    public void Build_ThrowsWhenProcessLoopbackConfigured()
    {
        AssertEx.True(OperatingSystem.IsWindows(), "[RunOn(OS.Windows)] did not engage.");

        // The trap that makes BuildAsync mandatory: process-loopback activation is asynchronous, so the
        // synchronous Build refuses rather than returning a recorder that was never activated.
        var builder = new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, WindowsProcessAudioCaptureSource.CaptureMode);

        _ = AssertEx.Throws<InvalidOperationException>(() => builder.Build().Dispose(),
            "Build() must refuse a process-loopback configuration, which is why the source calls BuildAsync.");
    }
}
