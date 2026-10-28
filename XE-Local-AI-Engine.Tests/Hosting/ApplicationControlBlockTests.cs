namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Reflection;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

// Smart App Control refuses an unsigned assembly with HRESULT 0x800711C7; startup must name the file, not crash.
[Category(TestCategories.Unit)]
public sealed class ApplicationControlBlockTests
{
    private const int BlockedByPolicy = unchecked((int)0x800711C7);
    private const string BlockedPath = @"C:\Users\tester\AppData\Local\XE\current\Quartz.Serialization.SystemTextJson.dll";

    [Test]
    public void TryDescribe_FindsTheBlockedFileOnADirectLoadFailure()
    {
        AssertEx.True(ApplicationControlBlock.TryDescribe(Blocked(), out var fileName), "The direct load failure is recognized.");
        AssertEx.Equal("Quartz.Serialization.SystemTextJson.dll", fileName);
    }

    [Test]
    public void TryDescribe_FindsTheBlockedFileThroughWrappers()
    {
        AssertEx.True(ApplicationControlBlock.TryDescribe(new TypeInitializationException("Quartz.Scheduler", Blocked()), out _),
            "Inside a type initializer failure.");
        AssertEx.True(ApplicationControlBlock.TryDescribe(new TargetInvocationException(new InvalidOperationException("outer", Blocked())), out _),
            "Two levels down an invocation failure.");
        AssertEx.True(ApplicationControlBlock.TryDescribe(new ReflectionTypeLoadException([null], [new InvalidOperationException(), Blocked()]), out var fileName),
            "Among a type load's loader exceptions.");
        AssertEx.Equal("Quartz.Serialization.SystemTextJson.dll", fileName);
    }

    [Test]
    public void TryDescribe_KeepsAnAssemblyDisplayNameWhole()
    {
        var blocked = new PolicyBlockedLoadException("Quartz.Serialization.SystemTextJson, Version=3.22.6.0, Culture=neutral, PublicKeyToken=null");

        AssertEx.True(ApplicationControlBlock.TryDescribe(blocked, out var fileName), "Recognized by code, whatever the name form.");
        AssertEx.Equal("Quartz.Serialization.SystemTextJson, Version=3.22.6.0, Culture=neutral, PublicKeyToken=null", fileName);
    }

    [Test]
    public void TryDescribe_IgnoresOtherLoadFailures()
    {
        // The control: the same exception types without the policy code are ordinary crashes.
        AssertEx.False(ApplicationControlBlock.TryDescribe(new FileLoadException("locked", BlockedPath), out _), "A load failure with another code.");
        AssertEx.False(ApplicationControlBlock.TryDescribe(new PolicyCodedException(), out _), "The code on an unrelated exception type.");
        AssertEx.False(ApplicationControlBlock.TryDescribe(null, out _), "No exception.");
    }

    [Test]
    public void Describe_WritesTheLineTheDesktopShellMatches()
    {
        var line = ApplicationControlBlock.Describe("Quartz.Serialization.SystemTextJson.dll");

        AssertEx.True(line.StartsWith("Application control blocked: Quartz.Serialization.SystemTextJson.dll", StringComparison.Ordinal),
            "The shell contract: the exact prefix, then the file name.");
        AssertEx.Contains(line, "Smart App Control");
        AssertEx.Contains(line, "install folder");
        AssertEx.False(line.Contains('\n', StringComparison.Ordinal), "One line.");
    }

    private static FileLoadException Blocked() =>
        new PolicyBlockedLoadException(BlockedPath);

    // The runtime raises a plain FileLoadException carrying the policy HRESULT; HResult has no public setter, so a
    // subclass stands in for the runtime's own constructor.
    private sealed class PolicyBlockedLoadException : FileLoadException
    {
        public PolicyBlockedLoadException(string fileName)
            : base("Could not load file or assembly. An Application Control policy has blocked this file. (0x800711C7)", fileName)
        {
            HResult = BlockedByPolicy;
        }
    }

    private sealed class PolicyCodedException : InvalidOperationException
    {
        public PolicyCodedException()
            : base("boom")
        {
            HResult = BlockedByPolicy;
        }
    }
}
