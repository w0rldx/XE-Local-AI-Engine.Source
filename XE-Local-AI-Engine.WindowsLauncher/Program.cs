using Velopack;
using XE_Local_AI_Engine.WindowsLauncher;

// Velopack delivers its lifecycle hooks (install/update/uninstall) to the packaged main exe — this launcher.
// Handle them and exit before any prerequisite validation or starting the managed host; on a normal run this is
// a no-op and control falls through to the launcher proper. Required for `vpk pack`, which statically verifies
// the main exe calls VelopackApp.Build().Run(). The managed host (the Client) still runs its own VelopackApp
// bootstrap for update management and desktop-mode detection.
VelopackApp.Build().Run();

return await WindowsLauncherApplication.RunAsync(args).ConfigureAwait(false);
