namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class McpServerServiceTests
{
    [Test]
    public async Task CreateAsync_WithValidStdioInput_PersistsAndDoesNotRefresh()
    {
        var service = CreateService(out var store, out var manager);
        var input = CreateStdioInput();
        var stored = CreateRecord(input, enabled: false);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(stored);

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.Equal(stored.Id, result.Id);
        AssertEx.False(result.Enabled, "A new registration is persisted disabled.");
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
        // Create persists disabled, so the enabled set is unchanged — no refresh.
        await manager.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithValidHttpLoopbackUrl_Persists()
    {
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput(url: "http://127.0.0.1:8931/sse");
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input, enabled: false));

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithBracketedIPv6LoopbackUrl_Persists()
    {
        // Uri.Host wraps an IPv6 literal in brackets ("[::1]"); the loopback allow-list stores the bare address ("::1"),
        // so the service must strip the brackets before the compare or a valid http://[::1]/ URL would be rejected.
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput(url: "http://[::1]:8931/sse");
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input, enabled: false));

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithEmptyName_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput(name: "   ");

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithStdioMissingCommand_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput(command: null);

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithHttpMissingUrl_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput(url: null);

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithNonLoopbackHttpUrl_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput(url: "http://10.0.0.5:8931/sse");

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithDuplicateName_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput(name: "Filesystem");
        // A registration with the same name (case-insensitive) already exists.
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([CreateRecord(CreateStdioInput(name: "filesystem"), enabled: false)]);

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SetEnabledAsync_WhenEnabling_TogglesAndTriggersRefresh()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: false) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.SetEnabledAsync(id, true, Arg.Any<CancellationToken>())
             .Returns(existing with
             {
                 Enabled = true,
                 Version = existing.Version + 1
             });

        var result = await service.SetEnabledAsync(id, enabled: true).ConfigureAwait(false);

        AssertEx.True(result!.Enabled, "Enabling must flip the persisted flag.");
        // The toggle goes through the dedicated store method, not a full UpdateAsync rebuild (no secret-column re-encrypt).
        await store.Received(1).SetEnabledAsync(id, true, Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await manager.Received(1).RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SetEnabledAsync_WhenAlreadyInThatState_IsNoOpAndDoesNotRefresh()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: true) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await service.SetEnabledAsync(id, enabled: true).ConfigureAwait(false);

        AssertEx.True(result!.Enabled);
        await store.DidNotReceive().SetEnabledAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await manager.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SetEnabledAsync_WhenServerMissing_ReturnsNull()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((McpServerRecord?)null);

        var result = await service.SetEnabledAsync(id, enabled: true).ConfigureAwait(false);

        AssertEx.Null(result);
        await manager.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_PreservesEnabledState_AndRefreshesWhenEnabled()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(name: "Filesystem"), enabled: true) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        store.UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => existing with
             {
                 Command = ((McpServerInput)callInfo[1]!).Command,
                 Enabled = ((McpServerInput)callInfo[1]!).Enabled
             });

        // The request body carries Enabled = false, but the service must preserve the current enabled (true).
        var requestInput = CreateStdioInput(name: "Filesystem", command: "npx-new") with
        {
            Enabled = false
        };
        var result = await service.UpdateAsync(id, requestInput).ConfigureAwait(false);

        AssertEx.True(result!.Enabled, "Update must preserve the current enabled state, not take it from the request body.");
        await store.Received(1).UpdateAsync(id, Arg.Is<McpServerInput>(input => input.Enabled), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        // The server is enabled, so a config change refreshes the live snapshot.
        await manager.Received(1).RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenDisabled_DoesNotRefresh()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(name: "Filesystem"), enabled: false) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        store.UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => existing with
             {
                 Command = ((McpServerInput)callInfo[1]!).Command
             });

        var result = await service.UpdateAsync(id, CreateStdioInput(name: "Filesystem", command: "npx-new")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await manager.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenServerMissing_ReturnsNull()
    {
        var service = CreateService(out var store, out _);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((McpServerRecord?)null);

        var result = await service.UpdateAsync(id, CreateStdioInput()).ConfigureAwait(false);

        AssertEx.Null(result);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SetEnabledAsync_WhenRefreshFaults_StillReturnsTheCommittedRecord()
    {
        // A refresh fault must never fail an already-committed CRUD mutation: the row is persisted and the next refresh
        // re-reconciles. The narrowed catch swallows the expected transient faults (here InvalidOperationException) and
        // logs, so the caller still sees its successful toggle.
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: false) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.SetEnabledAsync(id, true, Arg.Any<CancellationToken>()).Returns(existing with
        {
            Enabled = true
        });
        manager.RefreshAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new InvalidOperationException("connect failed"));

        var result = await service.SetEnabledAsync(id, enabled: true).ConfigureAwait(false);

        AssertEx.True(result!.Enabled, "The toggle is committed even though the post-change refresh faulted.");
        await manager.Received(1).RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SetEnabledAsync_WhenRefreshCancelled_PropagatesCancellation()
    {
        // OperationCanceledException is rethrown (not swallowed) so a caller-cancelled mutation surfaces the cancellation.
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: false) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.SetEnabledAsync(id, true, Arg.Any<CancellationToken>()).Returns(existing with
        {
            Enabled = true
        });
        manager.RefreshAsync(Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new OperationCanceledException());

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => service.SetEnabledAsync(id, enabled: true)).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteAsync_WhenEnabled_RefreshesAfterRemoval()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateRecord(CreateStdioInput(), enabled: true) with
        {
            Id = id
        });
        store.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var deleted = await service.DeleteAsync(id).ConfigureAwait(false);

        AssertEx.True(deleted);
        await manager.Received(1).RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteAsync_WhenDisabled_DoesNotRefresh()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateRecord(CreateStdioInput(), enabled: false) with
        {
            Id = id
        });
        store.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var deleted = await service.DeleteAsync(id).ConfigureAwait(false);

        AssertEx.True(deleted);
        await manager.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteAsync_WhenServerMissing_ReturnsFalse()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((McpServerRecord?)null);

        var deleted = await service.DeleteAsync(id).ConfigureAwait(false);

        AssertEx.False(deleted);
        await store.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await manager.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public void GetConnectionStatuses_DelegatesToConnectionManager()
    {
        var service = CreateService(out _, out var manager);
        var status = new McpServerConnectionStatus
        {
            ServerId = Guid.NewGuid(),
            Name = "Filesystem",
            Connected = true,
            ToolCount = 3,
            LastError = null,
            Tools = []
        };
        manager.GetStatuses().Returns([status]);

        var statuses = service.GetConnectionStatuses();

        AssertEx.ContainsSingle(statuses, entry => entry.ServerId == status.ServerId && entry.ToolCount == 3);
    }

    private static McpServerService CreateService(out IMcpServerStore store, out IMcpServerConnectionManager manager)
    {
        store = Substitute.For<IMcpServerStore>();
        manager = Substitute.For<IMcpServerConnectionManager>();
        var options = Options.Create(new McpOptions());
        return new McpServerService(store, manager, options, NullLogger<McpServerService>.Instance);
    }

    private static McpServerInput CreateStdioInput(string name = "Filesystem",
        string? command = "npx",
        bool enabled = false)
    {
        return new McpServerInput(name,
            Description: "A filesystem MCP server.",
            McpTransportKind.Stdio,
            command,
            Arguments: ["-y", "@modelcontextprotocol/server-filesystem"],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(StringComparer.Ordinal),
            Url: null,
            enabled);
    }

    private static McpServerInput CreateHttpInput(string? url, string name = "RemoteTools", bool enabled = false)
    {
        return new McpServerInput(name,
            Description: null,
            McpTransportKind.Http,
            Command: null,
            Arguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(StringComparer.Ordinal),
            url,
            enabled);
    }

    private static McpServerRecord CreateRecord(McpServerInput input, bool enabled)
    {
        return new McpServerRecord(Guid.NewGuid(),
            input.Name,
            input.Description,
            input.TransportKind,
            input.Command,
            input.Arguments,
            input.WorkingDirectory,
            input.Environment,
            input.Url,
            enabled,
            1,
            10,
            10);
    }
}
