namespace XE_Local_AI_Engine.Installer.Driver.Windows;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using XE_Local_AI_Engine.Installer.Cli;
using XE_Local_AI_Engine.Installer.Manifest;

/// <summary>
///     RC1 <see cref="IInstallerEnvironmentDriver" /> (plan §7.2). Drives the host by shelling out to
///     <c>wsl.exe</c> (hash-pinned <c>bash -s</c> transport — the script body rides stdin) and to the
///     vendored <c>*.ps1</c> via <c>powershell.exe -NoProfile -ExecutionPolicy Bypass -File</c> after
///     <c>Unblock-File</c> (HIGH-4). The driver is only instantiated at runtime under
///     <see cref="OperatingSystem.IsWindows" />, and the one Windows-only API (DPAPI) is guarded so the
///     file-layout logic stays testable cross-platform.
/// </summary>
public sealed partial class WindowsInstallerDriver : IInstallerEnvironmentDriver
{
    [GeneratedRegex("^sha256:[0-9a-fA-F]{64}$")]
    private static partial Regex ImageConfigIdRegex();

    private static readonly Regex ImageConfigIdPattern = ImageConfigIdRegex();

    private const string WslExecutable = "wsl.exe";
    private const string PowerShellExecutable = "powershell.exe";
    private const string DistroName = "xe-engine-runtime";
    private const string RootUser = "root";
    private const string RuntimeUser = "xe-engine";
    private const string StageImageScript = "stage-image.sh";
    private const string LoadImageScript = "load-image.sh";
    private const string PullModelScript = "pull-model.sh";
    private const string WriteManifestScript = "write-manifest.sh";
    private const string InstallPs1 = "install-host-agent.ps1";
    private const string UninstallPs1 = "uninstall-host-agent.ps1";

    private readonly IProcessRunner _processRunner;
    private readonly IInstallerHostConfigWriter _configWriter;
    private readonly long _minimumFreeDiskBytes;

    public WindowsInstallerDriver(
        IProcessRunner processRunner,
        IInstallerHostConfigWriter configWriter,
        long minimumFreeDiskBytes)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _configWriter = configWriter ?? throw new ArgumentNullException(nameof(configWriter));
        _minimumFreeDiskBytes = minimumFreeDiskBytes;
    }

    public async Task<WslProbeResult> ProbeAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var status = await _processRunner
            .RunAsync(WslExecutable, ["--status"], standardInput: null, cancellationToken)
            .ConfigureAwait(false);
        var list = await _processRunner
            .RunAsync(WslExecutable, ["--list", "--quiet"], standardInput: null, cancellationToken)
            .ConfigureAwait(false);

        var wslPresent = status.ExitCode == 0;
        var distroPresent = list.StandardOutput.Contains(DistroName, StringComparison.OrdinalIgnoreCase);
        var freeDisk = GetFreeDiskBytes(bundlePath);
        var requiredDisk = await ResolveRequiredFreeDiskBytesAsync(bundlePath, cancellationToken).ConfigureAwait(false);

        return new WslProbeResult
        {
            WslFeaturePresent = wslPresent,
            Wsl2Capable = wslPresent || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
            DistroPresent = distroPresent,
            FreeDiskBytes = freeDisk,
            RequiredFreeDiskBytes = requiredDisk
        };
    }

    private async Task<long> ResolveRequiredFreeDiskBytesAsync(string bundlePath, CancellationToken cancellationToken)
    {
        // code#2: the requirement comes from the bundle (rootfs + image + model sizes), not a hardcoded
        // constant. Fall back to the constructor default only if the metadata is missing/unreadable.
        try
        {
            var metadata = await BundleMetadata.LoadAsync(BundleLayout.MetadataPath(bundlePath), cancellationToken).ConfigureAwait(false);
            return metadata.MinimumFreeDiskBytes > 0 ? metadata.MinimumFreeDiskBytes : _minimumFreeDiskBytes;
        }
        catch (FileNotFoundException)
        {
            return _minimumFreeDiskBytes;
        }
        catch (InvalidOperationException)
        {
            return _minimumFreeDiskBytes;
        }
    }

    public async Task VerifyPayloadChecksumAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var checksumFile = Path.Combine(bundlePath, "SHA256SUMS");
        if (!File.Exists(checksumFile))
        {
            throw new FileNotFoundException("Bundle is missing SHA256SUMS.", checksumFile);
        }

        var lines = await File.ReadAllLinesAsync(checksumFile, cancellationToken).ConfigureAwait(false);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var (expectedHash, relativePath) = ParseChecksumLine(line);
            var targetPath = Path.Combine(bundlePath, relativePath);
            if (!File.Exists(targetPath))
            {
                throw new InvalidOperationException($"Payload file listed in SHA256SUMS is missing: {relativePath}");
            }

            var actualHash = await ComputeFileSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new BundleChecksumException($"Checksum mismatch for {relativePath}. Bundle is corrupt; aborting.");
            }
        }
    }

    public Task EnableWslAsync(CancellationToken cancellationToken = default) =>
        RunWslAsync(["--install", "--no-distribution"], standardInput: null, cancellationToken);

    public async Task ImportDistroAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        // Mirrors WslCommandAllowlist.Import: the distro name, install path, and rootfs tarball, version 2.
        var installPath = Path.Combine(bundlePath, "wsl", DistroName);
        Directory.CreateDirectory(installPath);
        var rootfs = BundleLayout.RootfsTarPath(bundlePath);
        await RunWslAsync(["--import", DistroName, installPath, rootfs, "--version", "2"], standardInput: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> LoadImageAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var metadata = await BundleMetadata.LoadAsync(BundleLayout.MetadataPath(bundlePath), cancellationToken).ConfigureAwait(false);

        // code#4: fail-closed before any in-distro execution if the bundle-recorded image identity is not a
        // well-formed config digest. The load script verifies the loaded Id against this value, so a
        // malformed/empty Id would otherwise reach `docker tag`/`docker inspect` with garbage.
        if (!ImageConfigIdPattern.IsMatch(metadata.ExpectedImageId))
        {
            throw new InvalidOperationException(
                $"bundle-metadata.json XE_EXPECTED_IMAGE_ID is not a valid sha256 config digest: '{metadata.ExpectedImageId}'.");
        }

        // Step 1 (stage): copy the image tar from the per-machine /mnt source to the fixed staging path.
        // The script body is hashed and verified; the per-machine source path rides stdin as a SECOND
        // line after the body (stage-image.sh does `read -r SRC_PATH`), so it never alters the hash.
        var stageScript = await ReadScriptAsync(bundlePath, StageImageScript, cancellationToken).ConfigureAwait(false);
        var imageMountPath = BundleLayout.ToWslMountPath(BundleLayout.ImageTarPath(bundlePath));
        await RunBashScriptAsync(RootUser, stageScript, metadata.StageImageScriptSha256, imageMountPath, cancellationToken)
            .ConfigureAwait(false);

        // Step 2 (load): docker load + retag + verify config Id. Fully static body (tokens were baked at
        // build time); no extra stdin.
        var loadScript = await ReadScriptAsync(bundlePath, LoadImageScript, cancellationToken).ConfigureAwait(false);
        await RunBashScriptAsync(RootUser, loadScript, metadata.LoadImageScriptSha256, extraStdinLine: null, cancellationToken)
            .ConfigureAwait(false);

        return metadata.ExpectedImageId;
    }

    public async Task<bool> IsPhaseSatisfiedAsync(InstallerPhaseProbe phase, string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        try
        {
            return phase switch
            {
                InstallerPhaseProbe.DistroImport => await IsDistroPresentAsync(cancellationToken).ConfigureAwait(false),
                InstallerPhaseProbe.ImageLoad => await IsImageLoadedAsync(bundlePath, cancellationToken).ConfigureAwait(false),
                _ => false
            };
        }
        catch (InvalidOperationException)
        {
            // A probe must never block install: on any probe error, treat the phase as not-yet-done so it
            // runs (the phase actions are themselves idempotent). Real failures surface from the action.
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> BuildTeardownInventoryAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var inventory = new List<string>
        {
            // Documented fixed paths (plan §3 invariant 1) — always owned by an install we created.
            $"WSL distro `{DistroName}` (all in-distro containers, volumes, models, DB, keys)",
            @"%ProgramData%\XE-Local-AI-Engine\",
            @"%ProgramFiles%\XE-Local-AI-Engine\",
            "Start-Menu + Desktop shortcuts (4)"
        };

        // Manifest-attributable container names, filtered through the shared ownership rule (plan §3
        // invariant 1). The vendored ps1 is the actual deletion enforcer; this is the human preview.
        var manifest = await TryLoadInstallerManifestAsync(bundlePath, cancellationToken).ConfigureAwait(false);
        if (manifest is not null)
        {
            foreach (var name in manifest.ContainerNames.Where(name => InstallerContainerOwnership.Owns(manifest, name)))
            {
                inventory.Add($"container `{name}` (manifest-owned)");
            }
        }

        return inventory;
    }

    public async Task WriteConfigAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        // Windows host-FS config: the DPAPI admin token + dirs (NOT runtime.json, NOT the manifest — HIGH-1).
        await _configWriter.WriteAsync(bundlePath, cancellationToken).ConfigureAwait(false);

        // HIGH-1 manifest delivery: the in-distro Linux host agent reads its runtime manifest from
        // $XDG_CONFIG_HOME/xe-host-agent/manifest.yaml (bound into HostAgent:Runtime config). Deliver the
        // bundle manifest there through the hash-pinned root `bash -s` seam: the static write-manifest.sh
        // writes the manifest content (fed on stdin after the hashed body) to the XDG config path. The
        // script + its `writeManifestScriptSha256` are owned by the packaging lane (worker-pkg); until that
        // companion lands the manifest cannot be delivered, so fail loudly rather than silently skip.
        await DeliverManifestToDistroAsync(bundlePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverManifestToDistroAsync(string bundlePath, CancellationToken cancellationToken)
    {
        var scriptPath = BundleLayout.InDistroScriptPath(bundlePath, WriteManifestScript);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                $"In-distro manifest-delivery script not found in bundle: {WriteManifestScript}. " +
                "The packaging lane must vendor it (write the manifest to $XDG_CONFIG_HOME/xe-host-agent/manifest.yaml) " +
                "and record its writeManifestScriptSha256 in bundle-metadata.json.",
                scriptPath);
        }

        var manifestSource = BundleLayout.ManifestPath(bundlePath);
        if (!File.Exists(manifestSource))
        {
            throw new FileNotFoundException("Bundle is missing manifest/managed.yaml.", manifestSource);
        }

        var metadata = await BundleMetadata.LoadAsync(BundleLayout.MetadataPath(bundlePath), cancellationToken).ConfigureAwait(false);
        var script = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        var manifestYaml = await File.ReadAllTextAsync(manifestSource, cancellationToken).ConfigureAwait(false);

        // The manifest content rides stdin AFTER the hashed script body (the script reads it via a heredoc
        // sentinel), so the per-bundle manifest never alters the script SHA — same discipline as stage-image.
        await RunBashScriptAsync(RootUser, script, metadata.WriteManifestScriptSha256, manifestYaml, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task InstallHostAgentAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var sourceDir = BundleLayout.HostAgentSourceDir(bundlePath);
        await RunVendoredPowerShellAsync(
            bundlePath,
            InstallPs1,
            ["-SourceDirectory", sourceDir],
            throwOnNonZero: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PullModelAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);

        var metadata = await BundleMetadata.LoadAsync(BundleLayout.MetadataPath(bundlePath), cancellationToken).ConfigureAwait(false);

        // Runs as the xe-engine (runtime) user; the model name was baked into the static body at build time.
        var pullScript = await ReadScriptAsync(bundlePath, PullModelScript, cancellationToken).ConfigureAwait(false);
        await RunBashScriptAsync(RuntimeUser, pullScript, metadata.PullModelScriptSha256, extraStdinLine: null, cancellationToken)
            .ConfigureAwait(false);

        return metadata.BootstrapModel;
    }

    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        // Post-install reachability check: the in-distro host-agent control reports admin status. A
        // non-zero exit (e.g. a port bound by a non-XE process) surfaces a clear diagnostic (MED-7c).
        var result = await _processRunner.RunAsync(
            WslExecutable,
            ["--distribution", DistroName, "--user", RuntimeUser, "--", "/opt/xe-host-agent/bin/xe-host-agent-ctl", "status"],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            var hint = detail.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                       || detail.Contains("bind", StringComparison.OrdinalIgnoreCase)
                ? " A required local port may be bound by another process; stop the conflicting service and re-run verify."
                : string.Empty;
            throw new InvalidOperationException(
                $"Post-install verification failed (exit {result.ExitCode}): {detail}{hint}");
        }
    }

    public async Task<TeardownResult> TeardownAsync(InstallerArguments arguments, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var bundlePath = arguments.BundlePath
                         ?? throw new InvalidOperationException("Teardown requires the bundle path that vendors the uninstall script.");

        // §7.4 flag mapping: -Mode managed always; dry-run → -WhatIf (the ps1 has NO -DryRun); a confirmed
        // run → -Force (the installer owns the typed gate, so the ps1 runs unattended). --keep-models → -KeepModels.
        var psArgs = new List<string> { "-Mode", "managed" };
        if (dryRun)
        {
            psArgs.Add("-WhatIf");
        }
        else
        {
            psArgs.Add("-Force");
        }

        if (arguments.KeepModels)
        {
            psArgs.Add("-KeepModels");
        }

        var result = await RunVendoredPowerShellAsync(bundlePath, UninstallPs1, psArgs, throwOnNonZero: false, cancellationToken)
            .ConfigureAwait(false);

        if (dryRun)
        {
            // A dry-run only inventories; completeness is meaningless here. Report "complete" so the
            // orchestrator's dry-run path is not mistaken for a partial teardown.
            return new TeardownResult { DistroRemoved = true, ProgramDataRemoved = true, ManifestRemoved = true, Residuals = [] };
        }

        return await AssertTeardownCompleteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TeardownResult> AssertTeardownCompleteAsync(ProcessRunResult teardownResult, CancellationToken cancellationToken)
    {
        var residuals = new List<string>();
        if (teardownResult.ExitCode != 0)
        {
            residuals.Add($"uninstall script exited {teardownResult.ExitCode}: {teardownResult.StandardError.Trim()}");
        }

        // Distro removal: the runtime distro must no longer be listed.
        var list = await _processRunner
            .RunAsync(WslExecutable, ["--list", "--quiet"], standardInput: null, cancellationToken)
            .ConfigureAwait(false);
        var distroRemoved = !list.StandardOutput.Contains(DistroName, StringComparison.OrdinalIgnoreCase);
        if (!distroRemoved)
        {
            residuals.Add($"{DistroName} distro still registered");
        }

        // ProgramData root removal (the host-agent fixed path).
        var programDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "XE-Local-AI-Engine");
        var programDataRemoved = !Directory.Exists(programDataRoot);
        if (!programDataRemoved)
        {
            residuals.Add($"{programDataRoot} still present");
        }

        return new TeardownResult
        {
            DistroRemoved = distroRemoved,
            ProgramDataRemoved = programDataRemoved,
            // The installer state store owns the manifest file; the orchestrator deletes it after a clean
            // teardown, so the driver reports it removed when the dangerous artifacts are gone.
            ManifestRemoved = distroRemoved && programDataRemoved,
            Residuals = residuals
        };
    }

    private async Task<ProcessRunResult> RunVendoredPowerShellAsync(
        string bundlePath,
        string scriptFileName,
        IReadOnlyList<string> scriptArguments,
        bool throwOnNonZero,
        CancellationToken cancellationToken)
    {
        var scriptPath = BundleLayout.VendoredScriptPath(bundlePath, scriptFileName);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Vendored script not found in bundle: {scriptFileName}", scriptPath);
        }

        // HIGH-4: strip the zip-propagated Zone.Identifier (the MOTW marker) before invoking, then run
        // under Windows PowerShell with profile disabled and ExecutionPolicy bypassed so a restrictive
        // machine policy cannot block the vendored script. `pwsh` is NOT assumed present on a clean box.
        // sec#1: the unblock is done by deleting the NTFS `Zone.Identifier` alternate data stream
        // DIRECTLY (the same effect as Unblock-File) rather than by building a PowerShell -Command string
        // from the path — eliminating any command-injection surface in this elevated process. Only the
        // script path (a bundle-fixed value) is ever passed to PowerShell, and only via -File / argument list.
        RemoveMarkOfTheWeb(scriptPath);

        var invokeArgs = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath };
        invokeArgs.AddRange(scriptArguments);
        return await RunPowerShellAsync(invokeArgs, throwOnNonZero, cancellationToken).ConfigureAwait(false);
    }

    private static void RemoveMarkOfTheWeb(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The MOTW lives in the `Zone.Identifier` NTFS alternate data stream; deleting it is exactly what
        // Unblock-File does. Absence (no marker) is the success case, so swallow not-found.
        var zoneStream = filePath + ":Zone.Identifier";
        try
        {
            File.Delete(zoneStream);
        }
        catch (FileNotFoundException)
        {
            // No MOTW present — already unblocked; nothing to strip.
        }
        catch (DirectoryNotFoundException)
        {
            // No MOTW present — already unblocked; nothing to strip.
        }
    }

    private async Task<ProcessRunResult> RunPowerShellAsync(IReadOnlyList<string> arguments, bool throwOnNonZero, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(PowerShellExecutable, arguments, standardInput: null, cancellationToken).ConfigureAwait(false);
        if (throwOnNonZero && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"`powershell.exe {string.Join(' ', arguments)}` failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result;
    }

    /// <summary>
    ///     Verify the script's SHA-256 against the bundle-recorded value (mirrors
    ///     <c>Wsl2Driver.VerifyScriptHash</c>: hex of SHA-256 over the UTF-8 script, compared
    ///     OrdinalIgnoreCase), then feed it to <c>wsl … --user &lt;user&gt; -- bash -s</c> on stdin. A
    ///     <paramref name="extraStdinLine" /> is appended after the body (and outside the hashed region)
    ///     for the stage step's per-machine source path.
    /// </summary>
    private async Task RunBashScriptAsync(
        string distroUser,
        string scriptText,
        string expectedSha256,
        string? extraStdinLine,
        CancellationToken cancellationToken)
    {
        VerifyScriptHash(scriptText, expectedSha256);

        var stdin = extraStdinLine is null
            ? scriptText
            : scriptText + "\n" + extraStdinLine + "\n";

        var result = await _processRunner.RunAsync(
            WslExecutable,
            ["--distribution", DistroName, "--user", distroUser, "--", "bash", "-s"],
            stdin,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"In-distro script (user {distroUser}) failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }

    private static void VerifyScriptHash(string script, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("In-distro script hash verification failed; refusing to execute.");
        }
    }

    private static Task<string> ReadScriptAsync(string bundlePath, string scriptFileName, CancellationToken cancellationToken)
    {
        var path = BundleLayout.InDistroScriptPath(bundlePath, scriptFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"In-distro script not found in bundle: {scriptFileName}", path);
        }

        return File.ReadAllTextAsync(path, cancellationToken);
    }

    private async Task RunWslAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(WslExecutable, arguments, standardInput, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"`wsl.exe {string.Join(' ', arguments)}` failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }

    private async Task<bool> IsDistroPresentAsync(CancellationToken cancellationToken)
    {
        var list = await _processRunner
            .RunAsync(WslExecutable, ["--list", "--quiet"], standardInput: null, cancellationToken)
            .ConfigureAwait(false);
        return list.StandardOutput.Contains(DistroName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsImageLoadedAsync(string bundlePath, CancellationToken cancellationToken)
    {
        var metadata = await BundleMetadata.LoadAsync(BundleLayout.MetadataPath(bundlePath), cancellationToken).ConfigureAwait(false);
        if (!ImageConfigIdPattern.IsMatch(metadata.ExpectedImageId))
        {
            return false;
        }

        // In-distro `docker image inspect <id>` exits 0 only when the image with that exact config Id is
        // already loaded — a cheap, side-effect-free probe (no bash -s, no SHA-pinned script needed).
        var result = await _processRunner.RunAsync(
            WslExecutable,
            ["--distribution", DistroName, "--user", RootUser, "--", "docker", "image", "inspect", metadata.ExpectedImageId],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private static async Task<InstallerManifest?> TryLoadInstallerManifestAsync(string bundlePath, CancellationToken cancellationToken)
    {
        var manifestPath = BundleLayout.ManifestPath(bundlePath);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var yaml = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return InstallerManifestParser.Parse(yaml);
    }

    private long GetFreeDiskBytes(string bundlePath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(bundlePath));
            if (string.IsNullOrEmpty(root))
            {
                return _minimumFreeDiskBytes;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return _minimumFreeDiskBytes;
        }
        catch (IOException)
        {
            return _minimumFreeDiskBytes;
        }
    }

    private static (string Hash, string RelativePath) ParseChecksumLine(string line)
    {
        // sha256sum format: "<hex>␠␠<path>" (two spaces) or "<hex> <path>".
        var trimmed = line.Trim();
        var separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new InvalidOperationException($"Malformed SHA256SUMS line: {line}");
        }

        var hash = trimmed[..separator];
        var path = trimmed[(separator + 1)..].TrimStart(' ', '*');
        return (hash, path.Replace('/', Path.DirectorySeparatorChar));
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
