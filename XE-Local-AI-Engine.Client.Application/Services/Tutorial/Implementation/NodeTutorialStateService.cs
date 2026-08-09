namespace XE_Local_AI_Engine.Client.Services.Tutorial.Implementation;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Identity-backed onboarding tour state. Persists the JSON array on <see cref="NodeUser.TutorialState" />
///     via the user manager, so it shares the single-admin identity row and needs no separate store.
/// </summary>
public sealed class NodeTutorialStateService : INodeTutorialStateService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim PersistenceLock = new(initialCount: 1, maxCount: 1);

    private readonly NodeIdentityDbContext _dbContext;
    private readonly ILogger<NodeTutorialStateService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly UserManager<NodeUser> _userManager;

    public NodeTutorialStateService(NodeIdentityDbContext dbContext,
        UserManager<NodeUser> userManager,
        TimeProvider timeProvider,
        ILogger<NodeTutorialStateService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<TutorialStateEntry>> GetEntriesAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var user = await _userManager.GetUserAsync(principal).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogWarning("Tutorial-state read for an authenticated principal that did not resolve to a node user.");
            return [];
        }

        return Deserialize(user.TutorialState);
    }

    public async Task<bool> SaveEntryAsync(ClaimsPrincipal principal, string key, TutorialStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await PersistenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var user = await _userManager.GetUserAsync(principal).ConfigureAwait(false);
            if (user is null)
            {
                _logger.LogWarning("Tutorial-state save for an authenticated principal that did not resolve to a node user.");
                return false;
            }

            // Authentication can load the user into this request's scoped DbContext before it reaches this lock.
            // Refresh that tracked entity so every serialized write merges against the latest tutorial JSON and
            // concurrency stamp rather than the snapshot captured while parallel requests were authorizing.
            await _dbContext.Entry(user).ReloadAsync(cancellationToken).ConfigureAwait(false);

            var trimmedKey = key.Trim();
            var currentEntries = Deserialize(user.TutorialState);
            if (status == TutorialStatus.Skipped && currentEntries.Any(entry =>
                    string.Equals(entry.Key, trimmedKey, StringComparison.Ordinal) && entry.Status == TutorialStatus.Completed))
            {
                return true;
            }

            var entries = currentEntries
                          .Where(entry => !string.Equals(entry.Key, trimmedKey, StringComparison.Ordinal))
                          .Append(new TutorialStateEntry(trimmedKey, status, _timeProvider.GetUtcNow().UtcDateTime))
                          .ToArray();

            user.TutorialState = Serialize(entries);

            var updateResult = await _userManager.UpdateAsync(user).ConfigureAwait(false);
            if (!updateResult.Succeeded)
            {
                _logger.LogWarning("Failed to persist tutorial state for the current user: {Errors}.",
                    string.Join("; ", updateResult.Errors.Select(static error => error.Description)));
                return false;
            }

            return true;
        }
        finally
        {
            PersistenceLock.Release();
        }
    }

    private static IReadOnlyList<TutorialStateEntry> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredEntry[]>(json, SerializerOptions);
            if (stored is null)
            {
                return [];
            }

            var entries = new List<TutorialStateEntry>(stored.Length);
            foreach (var entry in stored)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && TryParseStatus(entry.Status, out var status))
                {
                    entries.Add(new TutorialStateEntry(entry.Key, status, entry.AtUtc));
                }
            }

            return entries;
        }
        catch (JsonException)
        {
            // Corrupt persisted state is treated as "no tours seen" rather than failing the read; the next save rewrites it.
            return [];
        }
    }

    private static string Serialize(IReadOnlyList<TutorialStateEntry> entries)
    {
        var stored = entries
                     .Select(static entry => new StoredEntry
                     {
                         Key = entry.Key,
                         Status = ToWireStatus(entry.Status),
                         AtUtc = entry.AtUtc
                     })
                     .ToArray();

        return JsonSerializer.Serialize(stored, SerializerOptions);
    }

    private static string ToWireStatus(TutorialStatus status)
    {
        return status switch
        {
            TutorialStatus.Completed => "completed",
            TutorialStatus.Skipped => "skipped",
            _ => "skipped"
        };
    }

    private static bool TryParseStatus(string? value, out TutorialStatus status)
    {
        switch (value)
        {
            case "completed":
                status = TutorialStatus.Completed;
                return true;
            case "skipped":
                status = TutorialStatus.Skipped;
                return true;
            default:
                status = TutorialStatus.Skipped;
                return false;
        }
    }

    private sealed record StoredEntry
    {
        public string? Key { get; init; }

        public string? Status { get; init; }

        public DateTime AtUtc { get; init; }
    }
}
