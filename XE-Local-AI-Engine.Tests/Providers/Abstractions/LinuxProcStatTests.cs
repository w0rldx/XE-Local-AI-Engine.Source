namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The state field of one stat line: only a signal stop (T) counts as stopped, a tracing stop (t) does not.</summary>
[Category(TestCategories.Unit)]
public sealed class LinuxProcStatTests
{
    [Test]
    [Arguments("T", true, false)]
    [Arguments("t", false, false)]
    [Arguments("S", false, false)]
    [Arguments("Z", false, true)]
    public async Task Read_ParsesTheStoppedAndExitedStates(string state, bool stopped, bool exited)
    {
        using var temp = new TempDirectory("xe-proc-stat");
        var path = temp.FilePath("stat");
        var fields = string.Join(' ', Enumerable.Repeat("0", 15));
        await File.WriteAllTextAsync(path, $"42 (a (b) c) {state} 7 42 42 {fields} 123456 0 0");

        var stat = LinuxProcStat.Read(path);

        AssertEx.Equal(LinuxProcStatOutcome.Parsed, stat.Outcome);
        AssertEx.Equal(stopped, stat.Stopped);
        AssertEx.Equal(exited, stat.Exited);
        AssertEx.Equal(7, stat.ParentProcessId);
        AssertEx.Equal(123456L, stat.StartTicks);
    }
}
