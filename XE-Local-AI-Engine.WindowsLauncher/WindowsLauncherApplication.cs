namespace XE_Local_AI_Engine.WindowsLauncher;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class WindowsLauncherApplication
{
    internal const string LauncherProcessIdVariable = "XE_WINDOWS_LAUNCHER_PID";
    private const string AspNetCoreRuntimeName = "Microsoft.AspNetCore.App";
    private const int MissingPrerequisiteExitCode = 150;
    private const int LaunchFailureExitCode = 151;
    private const string ManagedEntryPoint = "XE-Local-AI-Engine.Client.dll";
    private const string RuntimeConfig = "XE-Local-AI-Engine.Client.runtimeconfig.json";

    private static readonly Uri DownloadUri = new UriBuilder(Uri.UriSchemeHttps, "dotnet.microsoft.com")
    {
        Path = "en-us/download/dotnet/10.0"
    }.Uri;

    private static readonly string[] RequiredPayloadFiles =
    [
        ManagedEntryPoint,
        "XE-Local-AI-Engine.Client.deps.json",
        RuntimeConfig,
        "appsettings.AppUpdate.json",
        "wwwroot/index.html",
        "LICENSE",
        "NOTICE"
    ];

    internal static async Task<int> RunAsync(string[] arguments)
    {
        // Reached only on a normal launch: a Velopack lifecycle-hook invocation is handled by VelopackApp.Run() in
        // Main (which exits the process internally) and never gets here. Recording this proves the launcher started and
        // Velopack did not consume the launch, so a subsequent silent exit can be localized to the managed host.
        StartupDiagnostics.Record("Launcher started (Velopack lifecycle hooks not triggered).");

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return Fail("The Windows portable launcher requires 64-bit Windows.", LaunchFailureExitCode);
        }

        var baseDirectory = AppContext.BaseDirectory;
        var missing = MissingPayloadFiles(relative => File.Exists(Path.Combine(baseDirectory, relative)));
        if (missing.Count > 0)
        {
            return Fail($"The portable package is incomplete. Missing: {string.Join(", ", missing)}", LaunchFailureExitCode);
        }

        Version requiredRuntime;
        try
        {
            requiredRuntime = ResolveRequiredAspNetCoreRuntime(await File.ReadAllTextAsync(Path.Combine(baseDirectory, RuntimeConfig)).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return Fail($"The application runtime configuration is invalid: {exception.Message}", LaunchFailureExitCode);
        }

        var dotnet = ResolveDotNetHost();

        string runtimeInventory;
        try
        {
            runtimeInventory = await CaptureRuntimeInventoryAsync(dotnet).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
                                              or Win32Exception)
        {
            return Fail($"The installed .NET host could not be inspected: {exception.Message}", MissingPrerequisiteExitCode);
        }

        if (!HasCompatibleAspNetCoreRuntime(runtimeInventory, requiredRuntime))
        {
            return MissingRuntime(requiredRuntime);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotnet,
                WorkingDirectory = baseDirectory,
                UseShellExecute = false
            }
        };
        foreach (var argument in CreateManagedArguments(Path.Combine(baseDirectory, ManagedEntryPoint), arguments))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        // Explicit --mcp-only/--desktop arguments are forwarded above and take precedence over this default.
        process.StartInfo.Environment["XE_LAUNCH_MODE"] = "desktop";
        process.StartInfo.Environment[LauncherProcessIdVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        try
        {
            if (!process.Start())
            {
                return Fail("Windows did not start the managed application.", LaunchFailureExitCode);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);

            // The managed host inherits this console, so a non-zero exit whose cause never reached disk (a crash before
            // its Serilog file sink is built) would otherwise leave only a vanished console. Record the code so the
            // launcher.log always shows how the child ended, even when the managed side wrote nothing itself.
            if (process.ExitCode != 0)
            {
                StartupDiagnostics.Record($"Managed application exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.");
            }

            return process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return Fail($"The managed application could not be started: {exception.Message}", LaunchFailureExitCode);
        }
    }

    internal static IReadOnlyList<string> MissingPayloadFiles(Func<string, bool> fileExists) =>
        RequiredPayloadFiles.Where(relative => !fileExists(relative)).ToArray();

    internal static IReadOnlyList<string> CreateManagedArguments(string managedEntryPoint, IEnumerable<string> arguments) =>
        [managedEntryPoint, .. arguments];

    internal static Version ResolveRequiredAspNetCoreRuntime(string runtimeConfig)
    {
        using var document = JsonDocument.Parse(runtimeConfig);
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions)
            || !runtimeOptions.TryGetProperty("frameworks", out var frameworks)
            || frameworks.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The application runtime configuration has no frameworks array.");
        }

        var aspNetCore = frameworks.EnumerateArray().FirstOrDefault(framework =>
            framework.TryGetProperty("name", out var name)
            && string.Equals(name.GetString(), AspNetCoreRuntimeName, StringComparison.Ordinal));
        if (aspNetCore.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"{AspNetCoreRuntimeName} is missing.");
        }

        var version = aspNetCore.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString()
            : null;
        return Version.TryParse(version, out var parsed)
            ? parsed
            : throw new InvalidDataException($"{AspNetCoreRuntimeName} has an invalid version.");
    }

    internal static bool HasCompatibleAspNetCoreRuntime(string inventory, Version required)
    {
        foreach (var line in inventory.Split(['\r', '\n'],
                                          StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Where(line => line.StartsWith($"{AspNetCoreRuntimeName} ", StringComparison.Ordinal)))
        {
            var versionText = line[AspNetCoreRuntimeName.Length..].TrimStart().Split(' ', 2)[0];
            if (Version.TryParse(versionText, out var installed)
                && installed.Major == required.Major
                && installed.Minor == required.Minor
                && installed.Build >= required.Build)
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveDotNetHost()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")
        };
        foreach (var root in candidates.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            var candidate = Path.Combine(root!, "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet.exe";
    }

    private static async Task<string> CaptureRuntimeInventoryAsync(string dotnet)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotnet,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("--list-runtimes");
        if (!process.Start())
        {
            throw new InvalidOperationException("dotnet --list-runtimes did not start.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await standardError.ConfigureAwait(false));
        }

        return await standardOutput.ConfigureAwait(false);
    }

    private static int MissingRuntime(Version required)
    {
        StartupDiagnostics.Record($"Missing prerequisite: Microsoft.AspNetCore.App {required.Major}.{required.Minor}.{required.Build}+ (x64) is not installed.");
        Console.Error.WriteLine($"XE Local AI Engine requires Microsoft ASP.NET Core Runtime {required.Major}.{required.Minor}.{required.Build} "
                                + "or a newer .NET 10 servicing patch (x64). The runtime is not bundled with the Windows portable package.");
        Console.Error.WriteLine($"Download it from {DownloadUri} and then start the application again.");
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(DownloadUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Win32Exception)
        {
            // The URL is already printed; failure to open a browser does not change the prerequisite verdict.
        }

        return MissingPrerequisiteExitCode;
    }

    private static int Fail(string message, int exitCode)
    {
        Console.Error.WriteLine(message);
        StartupDiagnostics.Record($"Launch failed (exit {exitCode.ToString(CultureInfo.InvariantCulture)}): {message}");
        return exitCode;
    }
}
