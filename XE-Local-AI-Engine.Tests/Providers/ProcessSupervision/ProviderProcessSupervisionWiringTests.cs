namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetArchTest.Rules;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the four providers that supervise a child process bind the shared containment in
///     <c>Providers.ProcessSupervision</c> rather than a private copy, and that each runtime's stale reaper survives
///     registration. <see cref="ProviderLauncherSharedHandleTests" /> proves the same at run time.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ProviderProcessSupervisionWiringTests
{
    private const string SharedNamespace = "XE_Local_AI_Engine.Providers.ProcessSupervision";

    /// <summary>
    ///     The compiled launchers and runners bind the shared handle types. The Python runner owns its handle privately,
    ///     so it cannot be observed through a return value; its IL is the evidence.
    /// </summary>
    [Test]
    [Arguments(typeof(LlamaServerProcessLauncher), "LinuxProcessGroupHandle")]
    [Arguments(typeof(LlamaServerProcessLauncher), "WindowsJobObjectProcessHandle")]
    [Arguments(typeof(LlamaServerProcessLauncher), "PlainProcessHandle")]
    [Arguments(typeof(ImageServerProcessLauncher), "LinuxProcessGroupHandle")]
    [Arguments(typeof(ImageServerProcessLauncher), "WindowsJobObjectProcessHandle")]
    [Arguments(typeof(ImageServerProcessLauncher), "PlainProcessHandle")]
    [Arguments(typeof(ImageServerProcessLauncher), "ProcessStderrTail")]
    [Arguments(typeof(WhisperServerProcessLauncher), "LinuxProcessGroupHandle")]
    [Arguments(typeof(WhisperServerProcessLauncher), "WindowsJobObjectProcessHandle")]
    [Arguments(typeof(WhisperServerProcessLauncher), "PlainProcessHandle")]
    [Arguments(typeof(WhisperServerProcessLauncher), "ProcessStderrTail")]
    [Arguments(typeof(LinuxPythonToolRunner), "LinuxProcessGroupHandle")]
    public void LauncherOrRunner_DependsOnTheSharedType(Type consumer, string sharedType)
    {
        var result = Types.InAssembly(consumer.Assembly)
                          .That()
                          .HaveName(consumer.Name)
                          .Should()
                          .HaveDependencyOn($"{SharedNamespace}.{sharedType}")
                          .GetResult();

        AssertEx.True(result.IsSuccessful,
            $"{consumer.Name} no longer uses the shared {sharedType}; a private copy of process containment has come back.");
    }

    /// <summary>
    ///     All three runtimes register the same reaper type. <c>AddHostedService</c> dedupes by implementation type, so a
    ///     registration through it would leave only the first runtime's reaper and silently stop reaping the others.
    /// </summary>
    [Test]
    public void EveryRuntimeRegistersItsOwnStaleReaper()
    {
        var services = new ServiceCollection();
        services.AddLlamaServerLocalModelProvider();
        services.AddStableDiffusionCppImageRuntime();
        services.AddWhisperCppRuntime();

        var reapers = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                                   && descriptor.ImplementationFactory?.Method.ReturnType == typeof(StaleProcessReaper))
                              .ToList();

        AssertEx.Equal(expected: 3, reapers.Count,
            "Expected one stale reaper each for llama-server, sd-server and whisper-server.");
    }
}
