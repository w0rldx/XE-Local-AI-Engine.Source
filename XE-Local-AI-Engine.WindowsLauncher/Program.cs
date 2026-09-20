namespace XE_Local_AI_Engine.WindowsLauncher;

using Velopack;

internal static class Program
{
    // VelopackApp.Build().Run() MUST stay the first statement of a SYNCHRONOUS Main: an async entry point buries it in a state machine, which trips `vpk pack`'s
    // verifier ("does not look like your application's entry point") and breaks the contract that the lifecycle hooks run before any app code. Async work comes after.
    private static int Main(string[] args)
    {
        VelopackApp.Build().Run();

#pragma warning disable MA0045 // Velopack requires a synchronous Main (see above); the async work is awaited here by design.
        return WindowsLauncherApplication.RunAsync(args).GetAwaiter().GetResult();
#pragma warning restore MA0045
    }
}
