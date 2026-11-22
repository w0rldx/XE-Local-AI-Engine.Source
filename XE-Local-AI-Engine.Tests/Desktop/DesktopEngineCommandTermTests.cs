namespace XE_Local_AI_Engine.Tests.Desktop;

using System.Diagnostics;
using System.Globalization;
using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Desktop.Linux;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>The engine command's polite stop over a scripted pidfd and <c>kill(2)</c>, around a real child that nothing scripted signals.</summary>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
public sealed class DesktopEngineCommandTermTests
{
    /// <summary>A filter can allow <c>pidfd_open</c> yet refuse <c>pidfd_send_signal</c> while <c>kill(2)</c> still works.</summary>
    [Test]
    [Arguments(LinuxPidFdSendStatus.Failed, true)]
    [Arguments(LinuxPidFdSendStatus.Sent, false)]
    [Arguments(LinuxPidFdSendStatus.Gone, false)]
    public async Task SendTerm_WhenThePidFdSendDidNotGoOut_FallsBackToTheExitGuardedNumericTerm(LinuxPidFdSendStatus sent, bool numeric)
    {
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = "sleep",
            ArgumentList = { "30" },
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("sleep did not start.");
        var signals = new List<string>();

        try
        {
            DesktopEngineCommand.SendTerm(child,
                static _ => new LinuxPidFd(4096, ownsHandle: false, LinuxPidFdOpenStatus.Opened),
                (_, signal) =>
                {
                    signals.Add(string.Create(CultureInfo.InvariantCulture, $"send {signal}"));
                    return new LinuxPidFdSendResult(sent, sent == LinuxPidFdSendStatus.Failed ? 1 : 0);
                },
                (pid, signal) =>
                {
                    signals.Add(string.Create(CultureInfo.InvariantCulture, $"kill {pid} {signal}"));
                    return 0;
                });

            var expected = numeric ? $"send 15,kill {child.Id} 15" : "send 15";
            AssertEx.Equal(expected, string.Join(',', signals), "only a send that did not go out takes the numeric TERM");
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }
}
