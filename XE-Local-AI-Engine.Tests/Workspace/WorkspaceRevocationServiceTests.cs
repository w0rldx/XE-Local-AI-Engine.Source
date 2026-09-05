namespace XE_Local_AI_Engine.Tests.Workspace;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WorkspaceRevocationServiceTests
{
    [Test]
    public async Task RevokeAsync_WhenActive_PreparesBeforeSoftRevoke()
    {
        var store = Substitute.For<INodeSelectedFolderStore>();
        var preparation = Substitute.For<IWorkspaceRevocationPreparation>();
        var session = Substitute.For<IWorkspaceRevocationSession>();
        var id = Guid.NewGuid();
        var record = new SelectedFolderRecord(id, "repo", "/trusted/repo", SelectedFolderMode.ReadOnlyMount, CreatedAtUtc: 1);
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(record);
        store.RevokeAsync(id, Arg.Any<CancellationToken>()).Returns(true);
        preparation.PrepareAsync(Arg.Any<ResolvedSelectedFolder>(), Arg.Any<CancellationToken>()).Returns(session);
        var service = new WorkspaceRevocationService(store, preparation, NullLogger<WorkspaceRevocationService>.Instance);

        await service.RevokeAsync(id.ToString());

        Received.InOrder(() =>
        {
            _ = preparation.PrepareAsync(Arg.Is<ResolvedSelectedFolder>(folder => folder.Id == id), Arg.Any<CancellationToken>());
            _ = store.RevokeAsync(id, Arg.Any<CancellationToken>());
#pragma warning disable CA2012 // Inside a Received.InOrder query the returned ValueTask is a recording artefact, not a real async operation.
            _ = session.DisposeAsync();
#pragma warning restore CA2012
        });
        await session.Received(1).DisposeAsync();
    }

    [Test]
    public async Task RevokeAsync_WhenPreparationFails_DoesNotAdvertiseOrPersistRevocation()
    {
        var store = Substitute.For<INodeSelectedFolderStore>();
        var preparation = Substitute.For<IWorkspaceRevocationPreparation>();
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>())
             .Returns(new SelectedFolderRecord(id, "repo", "/trusted/repo", SelectedFolderMode.ReadOnlyMount, CreatedAtUtc: 1));
        preparation.PrepareAsync(Arg.Any<ResolvedSelectedFolder>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromException<IWorkspaceRevocationSession>(new InvalidOperationException("workspace clear failed")));
        var service = new WorkspaceRevocationService(store, preparation, NullLogger<WorkspaceRevocationService>.Instance);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => service.RevokeAsync(id.ToString()),
            "A clear/lease failure must fail closed.");

        _ = store.DidNotReceive().RevokeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RevokeAsync_HoldsPreparationSessionUntilStoreCommitCompletes()
    {
        var store = Substitute.For<INodeSelectedFolderStore>();
        var preparation = Substitute.For<IWorkspaceRevocationPreparation>();
        var session = Substitute.For<IWorkspaceRevocationSession>();
        var commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>())
             .Returns(new SelectedFolderRecord(id, "repo", "/trusted/repo", SelectedFolderMode.ReadOnlyMount, CreatedAtUtc: 1));
        store.RevokeAsync(id, Arg.Any<CancellationToken>()).Returns(commit.Task);
        preparation.PrepareAsync(Arg.Any<ResolvedSelectedFolder>(), Arg.Any<CancellationToken>()).Returns(session);
        var service = new WorkspaceRevocationService(store, preparation, NullLogger<WorkspaceRevocationService>.Instance);

        var revoke = service.RevokeAsync(id.ToString());
        await session.DidNotReceive().DisposeAsync();

        commit.SetResult(true);
        await revoke;

        await session.Received(1).DisposeAsync();
    }

    [Test]
    public async Task RevokeAsync_WhenStoreCommitFails_DisposesPreparationSessionExactlyOnce()
    {
        var store = Substitute.For<INodeSelectedFolderStore>();
        var preparation = Substitute.For<IWorkspaceRevocationPreparation>();
        var session = Substitute.For<IWorkspaceRevocationSession>();
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>())
             .Returns(new SelectedFolderRecord(id, "repo", "/trusted/repo", SelectedFolderMode.ReadOnlyMount, CreatedAtUtc: 1));
        store.RevokeAsync(id, Arg.Any<CancellationToken>())
             .Returns(Task.FromException<bool>(new InvalidOperationException("commit failed")));
        preparation.PrepareAsync(Arg.Any<ResolvedSelectedFolder>(), Arg.Any<CancellationToken>()).Returns(session);
        var service = new WorkspaceRevocationService(store, preparation, NullLogger<WorkspaceRevocationService>.Instance);

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => service.RevokeAsync(id.ToString()),
            "A failed soft-revoke commit must surface after releasing the lease-bearing session.");

        await session.Received(1).DisposeAsync();
    }

    [Test]
    public async Task RevokeAsync_CompetingRevocationRemainsBusyUntilFirstCommitAndSessionDisposal()
    {
        var id = Guid.NewGuid();
        var record = new SelectedFolderRecord(id, "repo", "/trusted/repo", SelectedFolderMode.ReadOnlyMount, CreatedAtUtc: 1);
        var store = new BlockingSelectedFolderStore(record);
        var preparation = new ExclusivePreparation();
        var service = new WorkspaceRevocationService(store, preparation, NullLogger<WorkspaceRevocationService>.Instance);

        var first = service.RevokeAsync(id.ToString());
        await store.CommitEntered.Task;

        _ = await AssertEx.ThrowsAsync<WorkspaceRevocationBusyException>(() => service.RevokeAsync(id.ToString()),
            "A competitor must remain busy while the first revocation is committing under the owner/node lease.");

        store.ReleaseCommit.SetResult(true);
        await first;
        AssertEx.Equal(expected: 1, preparation.DisposalCount);
    }

    [Test]
    public async Task RevokeAsync_WhenUnknownOrAlreadyRevoked_IsAnIndistinguishableNoOp()
    {
        var store = Substitute.For<INodeSelectedFolderStore>();
        var preparation = Substitute.For<IWorkspaceRevocationPreparation>();
        var service = new WorkspaceRevocationService(store, preparation, NullLogger<WorkspaceRevocationService>.Instance);

        await service.RevokeAsync(Guid.NewGuid().ToString());

        _ = preparation.DidNotReceive().PrepareAsync(Arg.Any<ResolvedSelectedFolder>(), Arg.Any<CancellationToken>());
        _ = store.DidNotReceive().RevokeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private sealed class BlockingSelectedFolderStore(SelectedFolderRecord record) : INodeSelectedFolderStore
    {
        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SelectedFolderRecord> AddAsync(string folderAlias,
            string hostPath,
            SelectedFolderMode mode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SelectedFolderRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<SelectedFolderRecord?>(record);

        public Task<SelectedFolderRecord?> GetByAliasAsync(string folderAlias, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SelectedFolderRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
        {
            CommitEntered.SetResult();
            return await ReleaseCommit.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ExclusivePreparation : IWorkspaceRevocationPreparation
    {
        private readonly object _gate = new();
        private int _disposalCount;
        private bool _held;

        public int DisposalCount => Volatile.Read(ref _disposalCount);

        public Task<IWorkspaceRevocationSession> PrepareAsync(ResolvedSelectedFolder folder, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_held)
                {
                    throw new WorkspaceRevocationBusyException();
                }

                _held = true;
            }

            return Task.FromResult<IWorkspaceRevocationSession>(new Session(this));
        }

        private sealed class Session(ExclusivePreparation owner) : IWorkspaceRevocationSession
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
                {
                    _ = Interlocked.Increment(ref owner._disposalCount);
                    lock (owner._gate)
                    {
                        owner._held = false;
                    }
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
