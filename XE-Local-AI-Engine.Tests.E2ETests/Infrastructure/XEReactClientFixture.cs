namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TUnit.Core.Interfaces;

/// <summary>
///     Picks a free loopback port up front, runs <c>pnpm install --frozen-lockfile</c> +
///     <c>pnpm run build</c> in the XE React client directory with <c>VITE_API_URL</c> baked to
///     that port, then copies the built <c>dist/</c> tree into <c>&lt;TempRoot&gt;</c> itself so the
///     factory's <c>UseWebRoot(TempRoot)</c> makes it available to <c>MapFallbackToFile</c>
///     and <c>UseStaticFiles</c> at the same origin. Post-cutover the React client owns root, so the
///     shell lives at the web-root rather than under an <c>/app</c> prefix.
///     No vite-preview server is started — the .NET host serves the SPA.
///     <para>
///         Concurrency safety: <see cref="BuildLock" /> serialises all install + build invocations
///         process-wide so parallel fixture instances never touch the shared React client directory at
///         the same time. <c>pnpm install --frozen-lockfile</c> is non-destructive (does not wipe
///         node_modules) and idempotent when the lockfile is already satisfied.
///     </para>
/// </summary>
public sealed class XEReactClientFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    ///     Process-wide lock that serialises pnpm install + build so concurrent fixture instances
    ///     never race on the shared React client directory.
    /// </summary>
    private static readonly SemaphoreSlim BuildLock = new(initialCount: 1, maxCount: 1);

    /// <summary>Free loopback port chosen before the React build so it can be baked into VITE_API_URL.</summary>
    public int Port { get; private set; }

    /// <summary>
    ///     Temp directory that contains the freshly built dist at its root.
    ///     Pass this to <see cref="XENodeE2EWebApplicationFactory" /> as the web root so that
    ///     <c>&lt;TempRoot&gt;/index.html</c> is found by <c>MapFallbackToFile</c>.
    /// </summary>
    public string TempRoot { get; private set; } = string.Empty;

    public ValueTask DisposeAsync()
    {
        if (!string.IsNullOrEmpty(TempRoot) && Directory.Exists(TempRoot))
        {
            try
            {
                Directory.Delete(TempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; temp files are reclaimed by the OS regardless.
            }
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        Port = GetFreePort();
        TempRoot = Path.Combine(Path.GetTempPath(), $"xe-e2e-webroot-{Guid.NewGuid():N}");

        // Post-cutover the React shell is served at root, so the dist is copied to the web-root
        // (TempRoot) itself rather than a TempRoot/app/ sub-directory.
        Directory.CreateDirectory(TempRoot);

        var clientDir = FindReactClientDirectory();

        // Serialise install + build across all concurrent fixture instances so pnpm never
        // races on the shared React client directory.
        await BuildLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Install with --frozen-lockfile: non-destructive (does not wipe node_modules),
            // idempotent when already installed, and honours the committed pnpm-lock.yaml.
            // One retry handles transient ENOENT / pnpm store contention.
            try
            {
                await RunProcessAsync("pnpm", "install --frozen-lockfile", clientDir, timeoutMs: 300_000).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(2_000).ConfigureAwait(false);
                await RunProcessAsync("pnpm", "install --frozen-lockfile", clientDir, timeoutMs: 300_000).ConfigureAwait(false);
            }

            // "build:e2e" is a bare `vite build`; the default "build" script prefixes it with tsc --noEmit +
            // biome + stylelint. The fixture only consumes dist/, never the lint output, and project-validate.sh
            // already runs `pnpm run lint` over the same tree — so paying for it here was 20-45s of duplicated
            // work per run. NOTE: this means a type error no longer fails at fixture init; the frontend lint gate
            // is now solely `pnpm run lint` (run it before cutting an RC).
            await RunProcessAsync("pnpm",
                "run build:e2e",
                clientDir,
                timeoutMs: 300_000,
                new Dictionary<string, string>
                {
                    ["VITE_API_URL"] = $"http://127.0.0.1:{Port}",
                    ["VITE_API_VERSION"] = "v1",
                    ["VITE_APP_TITLE"] = "XE E2E"
                }).ConfigureAwait(false);
        }
        finally
        {
            BuildLock.Release();
        }

        // vite outputs to dist/ by default; copy the contents into <TempRoot> itself
        // so the host resolves <TempRoot>/index.html at root via UseWebRoot(TempRoot).
        var distDir = Path.Combine(clientDir, "dist");
        CopyDirectory(distDir, TempRoot);

        if (!File.Exists(Path.Combine(TempRoot, "index.html")))
        {
            throw new InvalidOperationException($"React build did not produce index.html at '{TempRoot}'. " +
                                                $"Check that '{distDir}' exists and the build succeeded.");
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindReactClientDirectory()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(XEReactClientFixture).Assembly.Location)
                          ?? throw new InvalidOperationException("Could not resolve test assembly directory.");

        var directory = new DirectoryInfo(assemblyDir);
        while (directory != null)
        {
            var repositoryCandidate = Path.Combine(directory.FullName, "XE-Local-AI-Engine.Client.React");
            if (Directory.Exists(repositoryCandidate) && File.Exists(Path.Combine(repositoryCandidate, "package.json")))
            {
                return repositoryCandidate;
            }

            var superprojectCandidate = Path.Combine(directory.FullName, "Apps", "XE-Local-AI-Engine", "XE-Local-AI-Engine.Client.React");
            if (Directory.Exists(superprojectCandidate) && File.Exists(Path.Combine(superprojectCandidate, "package.json")))
            {
                return superprojectCandidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find XE-Local-AI-Engine.Client.React from the test assembly path. " +
                                            "Ensure the test runs from the repository or its containing superproject worktree.");
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new InvalidOperationException($"Expected build output directory '{sourceDir}' does not exist. " +
                                                "Verify pnpm run build completed successfully.");
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static async Task RunProcessAsync(string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMs,
        Dictionary<string, string>? environmentOverrides = null)
    {
        // Windows: run the tool through cmd.exe rather than by bare name.
        //   `UseShellExecute = false` means CreateProcessW, which appends only ".exe" and does NOT
        //   consult PATHEXT. pnpm installs itself on Windows as shims — `pnpm` (extensionless),
        //   `pnpm.CMD`, `pnpm.ps1` — with the real binary content-addressed under the store at a
        //   path that changes with every pnpm version, so there is no `pnpm.exe` on PATH to find.
        //   Starting "pnpm" therefore threw Win32Exception "The system cannot find the file
        //   specified" inside InitializeAsync, before a single test ran, on a box where
        //   `pnpm --version` works perfectly from any shell — measured 2026-08-03, pnpm 11.18.0.
        //   Resolving the shim's full path is not enough either: CreateProcessW cannot execute a
        //   .CMD directly. cmd.exe is what makes the shim runnable, and it keeps stdout/stderr
        //   redirection working (UseShellExecute = true would not). This mirrors the guard the
        //   repo's own JS already carries at scripts/RunPackageTool.mjs (`shell: platform === "win32"`).
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : fileName,
            Arguments = OperatingSystem.IsWindows() ? $"/c {fileName} {arguments}" : arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environmentOverrides != null)
        {
            foreach (var (key, value) in environmentOverrides)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Failed to start process '{fileName} {arguments}'.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var cancellationSource = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cancellationSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException($"Process '{fileName} {arguments}' did not complete within {timeoutMs}ms.");
        }

        var standardOutput = await stdOutTask.ConfigureAwait(false);
        var standardError = await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var message = new StringBuilder();
            message.Append($"Process '{fileName} {arguments}' exited with code {process.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(standardOutput))
            {
                message.Append($" Output: {standardOutput}");
            }

            if (!string.IsNullOrWhiteSpace(standardError))
            {
                message.Append($" Error: {standardError}");
            }

            throw new InvalidOperationException(message.ToString());
        }
    }
}
