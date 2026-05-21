namespace XE_Local_AI_Engine.HostAgent.Windows;

using System.Net;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Windows.Wsl;

public static class Program
{
    public static void Main(string[] args)
    {
        var paths = HostAgentWindowsPaths.CreateDefault();
        Directory.CreateDirectory(paths.LogDirectory);

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        using var loggerProvider = new RotatingFileLoggerProvider(paths.LogDirectory);
        builder.Logging.AddProvider(loggerProvider);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IWindowsIdentityProvider, WindowsIdentityProvider>();
        builder.Services.AddSingleton<WindowsHostAgentAcl>();
        builder.Services.AddSingleton<IHostAgentSecretProtector, DpapiCurrentUserSecretProtector>();
        builder.Services.AddSingleton<HostAgentSecretStore>();
        builder.Services.AddSingleton<DesiredStateStore>();
        builder.Services.AddSingleton<HostAgentAdminService>();
        builder.Services.Configure<HostAgentLinuxGrpcOptions>(options =>
            HostAgentLinuxGrpcOptions.Bind(options, builder.Configuration));
        builder.Services.AddSingleton<IHostAgentLinuxClient, HostAgentLinuxGrpcClient>();
        builder.Services.Configure<HostAgentWslOptions>(options =>
            HostAgentWslOptions.Bind(options, builder.Configuration, paths));
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<HostAgentWslOptions>>().Value);
        builder.Services.AddSingleton<IWindowsProcessRunner, WindowsProcessRunner>();
        builder.Services.AddSingleton<Wsl2Driver>();
        builder.Services.AddHostedService<AdminTokenInitializationHostedService>();
        builder.Services.AddHostedService<RuntimeMetadataHostedService>();
        builder.Services.AddHostedService<WslSupervisorHostedService>();

        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();

        using var consoleControlHandler = WindowsConsoleControlHandler.Register(app.Lifetime);

        app.UseLocalAdminRequestGuards();
        app.MapLocalAdminEndpoints();

        app.Run();
    }
}
