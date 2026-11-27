namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>The kill(2) wrapper's contract against the running kernel: 0 on success, else the errno, also left for the caller.</summary>
[Category(TestCategories.Unit)]
public sealed class LinuxKillTests
{
    private const int Esrch = 3;

    [Test]
    [RunOn(OS.Linux)]
    public void Send_TheNullSignalToThisProcess_ReturnsZero()
    {
        AssertEx.Equal(0, LinuxKill.Send(Environment.ProcessId, 0));
    }

    /// <summary>A pid above every kernel's pid_max names no process.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public void Send_ToAPidNoProcessHolds_ReturnsEsrch_AndLeavesItAsTheLastError()
    {
        AssertEx.Equal(Esrch, LinuxKill.Send(int.MaxValue, 0));
        AssertEx.Equal(Esrch, Marshal.GetLastPInvokeError());
    }
}
