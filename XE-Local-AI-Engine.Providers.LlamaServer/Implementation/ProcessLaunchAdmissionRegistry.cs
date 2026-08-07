namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Lock-protected process-wide exact launch handoff with conservative orphan/unbound blocking.</summary>
public sealed class ProcessLaunchAdmissionRegistry : IProcessLaunchAdmissionRegistry
{
    private readonly Dictionary<ProcessLaunchAdmissionKey, Entry> _entries = [];
    private readonly Lock _sync = new();

    public ProcessLaunchAdmissionSnapshot Snapshot(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var requested = new ProcessLaunchAdmissionKey(modelName, role);
        lock (_sync)
        {
            return new ProcessLaunchAdmissionSnapshot(_entries
                                                      .Where(static pair => pair.Value.Admission is not null)
                                                      .Select(static pair => pair.Key)
                                                      .ToHashSet(),
                _entries.ContainsKey(requested),
                _entries.Values.Any(static entry => entry is { LaunchReferences: > 0, IsUnbound: true } or { IsOrphaned: true }));
        }
    }

    public IProcessLaunchAdmissionLease? Acquire(ProcessLaunchAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var key = Key(admission.ModelName, admission.Role);
        lock (_sync)
        {
            if (_entries.ContainsKey(key)
                || _entries.Values.Any(static entry => entry is { LaunchReferences: > 0, IsUnbound: true } or { IsOrphaned: true }))
            {
                return null;
            }

            var entry = new Entry(admission)
            {
                ConsumerReferences = 1
            };
            _entries.Add(key, entry);
            return new ConsumerLease(this, key, entry);
        }
    }

    public bool TryAcquire(ProcessLaunchAdmission admission, out IProcessLaunchAdmissionLease? lease)
    {
        lease = Acquire(admission);
        return lease is not null;
    }

    public bool TryBeginLaunch(string modelName,
        ModelRole role,
        out ProcessLaunchAdmission? admission,
        out IProcessLaunchTicket? ticket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var key = Key(modelName, role);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.LaunchReferences > 0 || existing.IsOrphaned)
                {
                    admission = null;
                    ticket = null;
                    return false;
                }

                existing.LaunchReferences++;
                admission = existing.Admission;
                ticket = new LaunchTicket(this, key, existing);
                return true;
            }

            // A direct launch carries no capacity reservation. Do not let one bypass an admission already published
            // for another key: its unreserved memory pressure could invalidate the exact footprint promised there.
            if (_entries.Values.Any(static entry => entry.Admission is not null))
            {
                admission = null;
                ticket = null;
                return false;
            }

            // A direct provider/test/profiling spawn has no capacity admission. It is represented explicitly as an
            // unbound in-flight entry and conservatively blocks all new capacity admissions until it settles.
            var unbound = new Entry(admission: null)
            {
                IsUnbound = true,
                LaunchReferences = 1
            };
            _entries.Add(key, unbound);
            admission = null;
            ticket = new LaunchTicket(this, key, unbound);
            return true;
        }
    }

    private void ReleaseConsumer(ProcessLaunchAdmissionKey key, Entry expected)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry) || !ReferenceEquals(entry, expected))
            {
                return;
            }

            entry.ConsumerReferences = Math.Max(0, entry.ConsumerReferences - 1);
            if (entry.ConsumerReferences == 0 && entry.LaunchReferences > 0)
            {
                entry.IsOrphaned = true;
            }

            RemoveIfReleased(key, entry);
        }
    }

    private void ReleaseLaunch(ProcessLaunchAdmissionKey key, Entry expected)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry) || !ReferenceEquals(entry, expected))
            {
                return;
            }

            entry.LaunchReferences = Math.Max(0, entry.LaunchReferences - 1);
            RemoveIfReleased(key, entry);
        }
    }

    private void RemoveIfReleased(ProcessLaunchAdmissionKey key, Entry entry)
    {
        if (entry.ConsumerReferences == 0 && entry.LaunchReferences == 0)
        {
            _entries.Remove(key);
        }
    }

    private static ProcessLaunchAdmissionKey Key(string modelName, ModelRole role) =>
        new(modelName, role);

    private sealed class Entry(ProcessLaunchAdmission? admission)
    {
        public ProcessLaunchAdmission? Admission { get; } = admission;
        public int ConsumerReferences { get; set; }
        public bool IsOrphaned { get; set; }
        public bool IsUnbound { get; set; }
        public int LaunchReferences { get; set; }
    }

    private sealed class ConsumerLease(ProcessLaunchAdmissionRegistry owner, ProcessLaunchAdmissionKey key, Entry entry)
        : IProcessLaunchAdmissionLease
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseConsumer(key, entry);
            }
        }
    }

    private sealed class LaunchTicket(ProcessLaunchAdmissionRegistry owner, ProcessLaunchAdmissionKey key, Entry entry)
        : IProcessLaunchTicket
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseLaunch(key, entry);
            }
        }
    }
}
