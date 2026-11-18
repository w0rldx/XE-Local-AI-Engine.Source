namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>A serving worker's receipt files: one per spawn, beside the work root the startup sweep deletes, so a dead host's worker is still found.</summary>
/// <remarks>
///     A row holds a receipt only while its job runs; a warm worker outlives its jobs, so its identity lives here. A file is deleted only once
///     its process is proven gone: an unreadable or still-live one stays, and Model Lab refuses new starts while it does.
/// </remarks>
internal static class ModelLabWarmReceipts
{
    private const string FilePrefix = "warm-";

    public static string Root(INodeDataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        return Path.Combine(dataDirectory.Root, "model-lab", "receipts");
    }

    public static string PathFor(INodeDataDirectory dataDirectory, string runToken) =>
        Path.Combine(Root(dataDirectory), FilePrefix + runToken + ".json");

    public static byte[] Serialize(TrainingLaunchReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(TrainingProcessReaper.ToPersisted(receipt), TrainingJson.Options);

    /// <summary>Owner-only directory, atomic owner-only file: the receipt names a process a later host will signal.</summary>
    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> receipt)
    {
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(Path.GetDirectoryName(path)!);
        await SecureFilePermissions.WriteAllBytesAtomicAsync(path, receipt, CancellationToken.None);
    }

    /// <summary>Every receipt file on disk. Only for the startup sweep: a live host's new spawn writes here, and its file is not an orphan's.</summary>
    /// <exception cref="IOException">The directory exists, or may, and could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory, or a parent, may not be read or traversed.</exception>
    public static IReadOnlyList<string> List(INodeDataDirectory dataDirectory)
    {
        try
        {
            // Listed directly: Directory.Exists also answers false when a parent cannot be traversed, which is not an empty directory.
            return Directory.GetFiles(Root(dataDirectory), FilePrefix + "*.json");
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>Reaps each of <paramref name="paths" />, deleting each file whose process is proven gone.</summary>
    /// <param name="paths">The receipt files to reap.</param>
    /// <param name="held">The receipt this host still holds in memory for a path, or null: reaped instead of the file, whose absence then proves nothing.</param>
    /// <param name="inspector">Reads and kills what a receipt names.</param>
    /// <param name="released">Gets each file that is gone: deleted here, or already by an earlier reap.</param>
    /// <param name="logger">Where each reap is logged.</param>
    /// <param name="ct">Ends the sweep.</param>
    /// <returns>The first file left in place, named by its pid, or by its path when it names no pid; <see langword="null" /> when none is.</returns>
    public static async Task<ModelLabUnconfirmedWorker?> ReapAsync(IEnumerable<string> paths,
        Func<string, byte[]?> held,
        ITrainingProcessInspector inspector,
        Action<string> released,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(held);
        ModelLabUnconfirmedWorker? unverified = null;
        foreach (var path in paths)
        {
            var heldReceipt = held(path);
            byte[] receipt;
            try
            {
                receipt = heldReceipt ?? await File.ReadAllBytesAsync(path, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    released(path);
                }
                else
                {
                    logger.LogWarning(exception, "The Model Lab worker receipt {Path} could not be read; it is kept.", path);
                    unverified ??= new ModelLabUnconfirmedWorker
                    {
                        Name = NameOf(path, parsed: null),
                        ReceiptPath = path,
                        HeldUnprobeable = false
                    };
                }

                continue;
            }

            var outcome = await TrainingProcessReaper.ReapOneAsync(inspector, Guid.Empty, receipt, static _ => Task.CompletedTask, logger, ct);
            if (outcome == TrainingReapOutcome.Verified)
            {
                BestEffortFile.TryDeleteFile(path);
                released(path);
            }
            else if (unverified is null)
            {
                var parsed = ParseOrNull(receipt);
                unverified = new ModelLabUnconfirmedWorker
                {
                    Name = NameOf(path, parsed),
                    ReceiptPath = heldReceipt is null ? path : null,
                    HeldUnprobeable = heldReceipt is not null && (parsed is null || !TrainingProcessReaper.CanIdentify(parsed))
                };
            }
        }

        return unverified;
    }

    /// <summary>"pid N" for a receipt that names one, with the group it leads when it can be probed; otherwise the file itself, which the operator must inspect and remove.</summary>
    private static string NameOf(string path, TrainingLaunchReceiptV1? parsed) =>
        parsed switch
        {
            null => "receipt file " + path,
            // A trainer runs setsid, so the group it leads is named by its pid, whatever group the receipt recorded before that.
            _ when TrainingProcessReaper.CanIdentify(parsed) => string.Create(CultureInfo.InvariantCulture, $"pid {parsed.Pid}, process group {parsed.Pid}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"pid {parsed.Pid}")
        };

    private static TrainingLaunchReceiptV1? ParseOrNull(byte[] receipt)
    {
        try
        {
            return receipt.Length > 0 ? JsonSerializer.Deserialize<TrainingLaunchReceiptV1>(receipt, TrainingJson.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
