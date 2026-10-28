namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The restart-time reconciliation of <see cref="LlamaCppSourceBuildService" />: keeps the installed-runtime record
///     and the source-build tree it names consistent, and validates a kept tree's identity and backend.
/// </summary>
internal sealed partial class LlamaCppSourceBuildReconciler
{
    private readonly IActiveSourceBuildSignal _activeSignal;
    private readonly string _cacheRoot;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILogger _logger;
    private readonly string _sourceBuildRoot;

    internal LlamaCppSourceBuildReconciler(IInstalledRuntimeStore installedRuntimeStore,
        IActiveSourceBuildSignal activeSignal,
        string cacheRoot,
        string sourceBuildRoot,
        ILogger logger)
    {
        _installedRuntimeStore = installedRuntimeStore;
        _activeSignal = activeSignal;
        _cacheRoot = cacheRoot;
        _sourceBuildRoot = sourceBuildRoot;
        _logger = logger;
    }

    /// <summary>
    ///     Brings the active and backup trees and the installed-runtime record back to one consistent state after a
    ///     restart, and sets the active-build signal from the record that is kept.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var active = Path.Combine(_sourceBuildRoot, "active");
        var backup = Path.Combine(_sourceBuildRoot, ".backup");
        var installed = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        var sourceRecord = installed?.SourceBuildPath is { Length: > 0 };

        if (!Directory.Exists(active) && !Directory.Exists(backup) && IsPreProvenanceLegacyRecord(installed))
        {
            if (await ValidateLegacyRecordAsync(installed!, ct).ConfigureAwait(false))
            {
                await WarnIfBackendNotLiveAsync(installed!.SourceBuildPath!, GpuVariant.Cuda, ct).ConfigureAwait(false);
                _activeSignal.SetActive(GpuVariant.Cuda);
                return;
            }

            await _installedRuntimeStore.DeleteAsync(ct).ConfigureAwait(false);
            _activeSignal.Clear();
            return;
        }

        var canonicalActiveBin = Path.Combine(active, "build", "bin");
        var recordTargetsActive = sourceRecord && PathsEqual(installed!.SourceBuildPath!, canonicalActiveBin);
        var activeMatches = recordTargetsActive && await TreeMatchesRecordAsync(active, installed!, ct).ConfigureAwait(false);
        var backupState = recordTargetsActive
            ? installed! with
            {
                SourceBuildPath = Path.Combine(backup, "build", "bin")
            }
            : installed!;
        var backupMatches = recordTargetsActive && await TreeMatchesRecordAsync(backup, backupState, ct).ConfigureAwait(false);

        // Reconciliation keeps the installed-runtime record and the tree it NAMES consistent. A record not naming the active tree — a prebuilt record with no source path, or
        // one pointing elsewhere — is NOT authority to delete it: read as authority, a fresh-database node's prebuilt record over the shared installed-runtime.json deletes both trees.
        if (!Directory.Exists(active) && !Directory.Exists(backup))
        {
            // Only a record that NAMES the active tree is self-healed away by its absence. A record pointing elsewhere
            // (a relocated data directory, the legacy source-cuda layout) describes a tree this method never looked at.
            await ClearSourceRecordAsync(recordTargetsActive, ct).ConfigureAwait(false);
        }
        else if (!recordTargetsActive)
        {
            _logger.LogWarning(
                "The installed-runtime record does not name the managed source build at {ActivePath} (recorded source-build path: {RecordedSourceBuildPath}); leaving the source-build directories in place.",
                active,
                installed?.SourceBuildPath ?? "(none)");
        }
        else if (Directory.Exists(active) && Directory.Exists(backup))
        {
            if (activeMatches)
            {
                DeleteDirectoryRequired(backup);
            }
            else if (backupMatches)
            {
                DeleteDirectoryRequired(active);
                _activeSignal.Clear();
                Directory.Move(backup, active);
                await WriteReconciledStateAsync(installed!, active, ct).ConfigureAwait(false);
            }
            else
            {
                DeleteDirectoryRequired(active);
                _activeSignal.Clear();
                DeleteDirectoryRequired(backup);
                await ClearSourceRecordAsync(sourceRecord, ct).ConfigureAwait(false);
            }
        }
        else if (Directory.Exists(backup))
        {
            if (backupMatches)
            {
                Directory.Move(backup, active);
                await WriteReconciledStateAsync(installed!, active, ct).ConfigureAwait(false);
            }
            else
            {
                DeleteDirectoryRequired(backup);
                _activeSignal.Clear();
                await ClearSourceRecordAsync(sourceRecord, ct).ConfigureAwait(false);
            }
        }
        else if (!activeMatches)
        {
            // The record names this tree and the tree is not what the record promises (a rebuild that died between the
            // swap and the record write, or a tampered tree). Replacing it is the record's own rebuild semantics.
            DeleteDirectoryRequired(active);
            _activeSignal.Clear();
            await ClearSourceRecordAsync(sourceRecord, ct).ConfigureAwait(false);
        }

        var current = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (current?.SourceBuildPath is { Length: > 0 })
        {
            if (recordTargetsActive && PathsEqual(current.SourceBuildPath, canonicalActiveBin))
            {
                await WarnIfBackendNotLiveAsync(canonicalActiveBin, current.Variant, ct).ConfigureAwait(false);
            }

            // The signal follows the kept record even when the device is not live: EnsureBinaryAsync serves a recorded
            // build regardless, so an unset signal would admit on another variant and spawn this one.
            _activeSignal.SetActive(current.Variant);
        }
    }

    private static async Task<bool> TreeMatchesRecordAsync(string tree, InstalledRuntimeState state, CancellationToken ct)
    {
        if (!Directory.Exists(tree)
            || state.SourceBuildPath is not { Length: > 0 }
            || !PathsEqual(state.SourceBuildPath, Path.Combine(tree, "build", "bin"))
            || state.SourceRepository is null
            || state.SourceCommit is null
            || state.SourceRevisionMode is null)
        {
            return false;
        }

        var server = Path.Combine(tree, "build", "bin", LlamaCppSourceBuildService.ManagedServerFileName);
        var fitHelper = Path.Combine(tree, "build", "bin", LlamaCppSourceBuildService.ManagedFitParamsFileName);
        var manifestPath = Path.Combine(tree, LlamaCppSourceBuildService.ManifestFileName);
        if (!File.Exists(server)
            || new FileInfo(server).LinkTarget is not null
            || !File.Exists(fitHelper)
            || new FileInfo(fitHelper).LinkTarget is not null
            || !File.Exists(manifestPath))
        {
            return false;
        }

        LlamaCppSourceBuildService.SourceBuildManifest? manifest;
        try
        {
            await using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            manifest = await JsonSerializer.DeserializeAsync<LlamaCppSourceBuildService.SourceBuildManifest>(manifestStream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return false;
        }

        var expectedSource = state.SourceSelection
                             ?? (string.Equals(state.SourceRepository, LlamaCppSourceBuildRequestValidation.OfficialRepository, StringComparison.Ordinal)
                                 ? LlamaCppSourceSelection.Official
                                 : LlamaCppSourceSelection.Custom);
        if (!LlamaCppSourceBuildRequestValidation.HasValidOfficialProvenance(expectedSource,
                state.SourceRepository,
                state.SourceRevisionMode,
                state.SourceRequestedCommit,
                state.SourceCommit)
            || manifest is null
            || !LlamaCppSourceBuildRequestValidation.HasValidOfficialProvenance(manifest.Source,
                manifest.Repository,
                manifest.RevisionMode,
                manifest.RequestedCommit,
                manifest.ResolvedCommit)
            || manifest.Variant != state.Variant
            || manifest.Source != expectedSource
            || manifest.RevisionMode != state.SourceRevisionMode
            || !string.Equals(manifest.Tag, state.Tag, StringComparison.Ordinal)
            || !string.Equals(manifest.Repository, state.SourceRepository, StringComparison.Ordinal)
            || !string.Equals(manifest.ResolvedCommit, state.SourceCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.RequestedCommit, state.SourceRequestedCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.BinarySha256, state.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            LlamaCppSourceBuildService.ValidateTreeLinks(tree);
        }
        catch (LlamaRuntimeException)
        {
            return false;
        }

        // Identity only (manifest, provenance, links, SHA-256). Liveness is checked separately: a GPU that is briefly
        // invisible at boot must never make a valid build look foreign and get it deleted.
        return await FileSha256.MatchesAsync(server, state.Sha256, ct).ConfigureAwait(false);
    }

    /// <summary>Warns when a kept source build whose identity matches its record cannot report its backend device.</summary>
    /// <remarks>
    ///     The tree and the record stay: the usual causes (a driver/library mismatch until reboot, lost WSL GPU
    ///     passthrough, a slow probe) are transient, and deleting would cost the operator a full rebuild.
    /// </remarks>
    private async Task WarnIfBackendNotLiveAsync(string binDirectory, GpuVariant variant, CancellationToken ct)
    {
        bool live;
        try
        {
            live = await ValidateBinaryBackendAsync(Path.Combine(binDirectory, LlamaCppSourceBuildService.ManagedServerFileName), variant, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            live = false;
        }

        if (!live)
        {
            _logger.LogWarning(
                "The source-built llama.cpp runtime in {BinDirectory} matches its record, but its {Variant} backend reported no usable device. It is kept; GPU inference may fail until the device is available again (for example after a reboot).",
                binDirectory,
                variant);
        }
    }

    private bool IsPreProvenanceLegacyRecord(InstalledRuntimeState? state)
    {
        return state is { SourceRepository: null, SourceCommit: null, SourceRevisionMode: null, SourceRequestedCommit: null, SourceSelection: null }
               && state.IsLegacyPinnedCuda(_cacheRoot);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async Task<bool> ValidateLegacyRecordAsync(InstalledRuntimeState state, CancellationToken ct)
    {
        var server = Path.Combine(state.SourceBuildPath!, LlamaCppSourceBuildService.ManagedServerFileName);
        var fitHelper = Path.Combine(state.SourceBuildPath!, LlamaCppSourceBuildService.ManagedFitParamsFileName);
        if (!File.Exists(server)
            || new FileInfo(server).LinkTarget is not null
            || !File.Exists(fitHelper)
            || new FileInfo(fitHelper).LinkTarget is not null)
        {
            return false;
        }

        try
        {
            LlamaCppSourceBuildService.ValidateTreeLinks(state.SourceBuildPath!);
        }
        catch (LlamaRuntimeException)
        {
            return false;
        }

        return await FileSha256.MatchesAsync(server, state.Sha256, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ValidateBinaryBackendAsync(string server, GpuVariant variant, CancellationToken ct)
    {
        if (!await RunValidationCommandAsync(server, "--version", expectedDevicePrefix: null, ct).ConfigureAwait(false))
        {
            return false;
        }

        return variant == GpuVariant.Cpu
               || await RunValidationCommandAsync(server, "--list-devices", variant == GpuVariant.Cuda ? "CUDA" : "Vulkan", ct).ConfigureAwait(false);
    }

    private static async Task<bool> RunValidationCommandAsync(string server, string argument, string? expectedDevicePrefix, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(server)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(server) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(argument);
        using var process = new Process
        {
            StartInfo = startInfo
        };
        if (!process.Start())
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SourceBuildPolicy.ShortCommandTimeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = string.Concat(await stdout.ConfigureAwait(false), "\n", await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
            {
                return false;
            }

            return expectedDevicePrefix is null || output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                                                         .Any(line => RecoveryDeviceLineRegex().IsMatch(line)
                                                                      && line.TrimStart().StartsWith(expectedDevicePrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            ProcessCaptureRunner.TryKill(process);
        }
    }

    private Task WriteReconciledStateAsync(InstalledRuntimeState state, string active, CancellationToken ct)
    {
        return _installedRuntimeStore.WriteAsync(state with
        {
            SourceBuildPath = Path.Combine(active, "build", "bin")
        }, ct);
    }

    private async Task ClearSourceRecordAsync(bool sourceRecord, CancellationToken ct)
    {
        if (sourceRecord)
        {
            await _installedRuntimeStore.DeleteAsync(ct).ConfigureAwait(false);
            _activeSignal.Clear();
        }
    }

    private static void DeleteDirectoryRequired(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LlamaRuntimeException("A prior source-build directory could not be reconciled safely.", exception);
        }
    }

    [GeneratedRegex(@"^\s*(?:CUDA|Vulkan)[0-9]+:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RecoveryDeviceLineRegex();
}
