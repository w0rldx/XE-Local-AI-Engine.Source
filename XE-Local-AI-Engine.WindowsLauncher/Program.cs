namespace XE_Local_AI_Engine.WindowsLauncher;

using Velopack;

internal static class Program
{
    // Velopack delivers its lifecycle hooks (install/update/uninstall/relaunch) to the packaged main exe — this
    // launcher — and handles the process exit for them internally. VelopackApp.Build().Run() MUST therefore be the
    // very first statement in a SYNCHRONOUS Main: `vpk pack` statically verifies the main exe makes this call, and an
    // async entry point (top-level statements ending in `await`) buries it inside a compiler-generated state machine
    // (Program+<<Main>$>::MoveNext), which trips that verifier ("does not look like your application's entry point")
    // and violates the documented contract that hooks run before any app code. Keep this method synchronous and this
    // call first; do the actual (async) launcher work only after it returns. The managed host (the Client) still runs
    // its own VelopackApp bootstrap for update management and desktop-mode detection.
    private static int Main(string[] args)
    {
        VelopackApp.Build().Run();

#pragma warning disable MA0045 // Velopack requires a synchronous Main (see above); the async work is awaited here by design.
        return WindowsLauncherApplication.RunAsync(args).GetAwaiter().GetResult();
#pragma warning restore MA0045
    }
}
