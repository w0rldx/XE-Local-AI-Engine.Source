namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Launches a real long-running child through each runtime provider's production launcher and asserts the handle it
///     returns is the shared containment type for the OS running the suite.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ProviderLauncherSharedHandleTests
{
    private const string SharedNamespace = "XE_Local_AI_Engine.Providers.ProcessSupervision";

    [Test]
    public void LlamaServerLauncher_ReturnsTheSharedHandleForThisOs()
    {
        var (executable, arguments) = LongRunningCommand();
        var launcher = new LlamaServerProcessLauncher(NullLogger<LlamaServerProcessLauncher>.Instance,
            new ChildProcessOutputTailRegistry(TimeProvider.System));

        using var handle = launcher.Launch(new LlamaServerLaunchSpec
        {
            ModelName = "test-model",
            Role = ModelRole.Chat,
            ExecutablePath = executable,
            Arguments = arguments,
            Port = 0,
            WorkingDirectory = Path.GetTempPath()
        });

        AssertSharedHandle(handle);
    }

    [Test]
    public void ImageServerLauncher_ReturnsTheSharedHandleForThisOs()
    {
        var (executable, arguments) = LongRunningCommand();
        var launcher = new ImageServerProcessLauncher(NullLogger<ImageServerProcessLauncher>.Instance, new ImageServerProgressBroker(),
            new ChildProcessOutputTailRegistry(TimeProvider.System));

        using var handle = launcher.Launch(new ImageServerLaunchSpec
        {
            ModelName = "test-model",
            ExecutablePath = executable,
            Arguments = arguments,
            Port = 0,
            WorkingDirectory = Path.GetTempPath()
        });

        AssertSharedHandle(handle);
    }

    [Test]
    public void WhisperServerLauncher_ReturnsTheSharedHandleForThisOs()
    {
        var (executable, arguments) = LongRunningCommand();
        var launcher = new WhisperServerProcessLauncher(NullLogger<WhisperServerProcessLauncher>.Instance,
            new ChildProcessOutputTailRegistry(TimeProvider.System));

        using var handle = launcher.Launch(new WhisperServerLaunchSpec
        {
            ModelId = "test-model",
            ExecutablePath = executable,
            Arguments = arguments,
            Port = 0,
            WorkingDirectory = Path.GetTempPath()
        });

        AssertSharedHandle(handle);
    }

    private static void AssertSharedHandle(IProcessTreeHandle handle)
    {
        var expected = "PlainProcessHandle";
        if (OperatingSystem.IsWindows())
        {
            expected = "WindowsJobObjectProcessHandle";
        }
        else if (OperatingSystem.IsLinux())
        {
            expected = "LinuxProcessGroupHandle";
        }

        AssertEx.Equal($"{SharedNamespace}.{expected}", handle.GetType().FullName);
    }

    /// <summary>A child that stays up until the handle tree-kills it, on whichever OS runs the suite.</summary>
    private static (string Executable, string[] Arguments) LongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "ping -n 600 127.0.0.1"])
            : ("/bin/sh", ["-c", "sleep 600"]);
}
