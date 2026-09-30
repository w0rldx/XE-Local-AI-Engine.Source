namespace XE_Local_AI_Engine.Tests.Mcp;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
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

        var result = await service.CreateAsync(input);

        AssertEx.Equal(stored.Id, result.Id);
        AssertEx.False(result.Enabled, "A new registration is persisted disabled.");
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>());
        // Create persists disabled, so the enabled set is unchanged — no refresh.
        await manager.DidNotReceive().RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithValidHttpLoopbackUrl_Persists()
    {
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput("http://127.0.0.1:8931/sse");
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input, enabled: false));

        var result = await service.CreateAsync(input);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithBracketedIPv6LoopbackUrl_Persists()
    {
        // Uri.Host wraps an IPv6 literal in brackets ("[::1]"); the loopback allow-list stores the bare address ("::1"),
        // so the service must strip the brackets before the compare or a valid http://[::1]/ URL would be rejected.
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput("http://[::1]:8931/sse");
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input, enabled: false));

        var result = await service.CreateAsync(input);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithEmptyName_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput("   ");

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithStdioMissingCommand_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput(command: null);

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithHttpMissingUrl_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput(null);

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithNonLoopbackHttpUrl_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput("http://10.0.0.5:8931/sse");

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithDuplicateName_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput();
        // A registration with the same name (case-insensitive) already exists.
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([CreateRecord(CreateStdioInput("filesystem"), enabled: false)]);

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WhenTheStoreReportsANameConflict_ThrowsValidationNamingTheServer()
    {
        // The pre-check passed, and a concurrent create won the unique Name index before this insert.
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput();
        var storeConflict = new McpServerNameConflictException("stored", new InvalidOperationException("unique"));
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns<McpServerRecord>(_ => throw storeConflict);

        var rejection = await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));

        AssertEx.Equal($"An MCP server named '{input.Name}' is already registered.", rejection.Message);
        AssertEx.True(ReferenceEquals(storeConflict, rejection.InnerException), "The store's conflict must stay the cause.");
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
        store.SetEnabledAsync(id, enabled: true, Arg.Any<CancellationToken>())
             .Returns(existing with
             {
                 Enabled = true,
                 Version = existing.Version + 1
             });

        var result = await service.SetEnabledAsync(id, enabled: true);

        AssertEx.True(result!.Enabled, "Enabling must flip the persisted flag.");
        // The toggle goes through the dedicated store method, not a full UpdateAsync rebuild (no secret-column re-encrypt).
        await store.Received(1).SetEnabledAsync(id, enabled: true, Arg.Any<CancellationToken>());
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
        await manager.Received(1).RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetEnabledAsync_WhenAlreadyDisabled_IsNoOpAndDoesNotRefresh()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: false) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await service.SetEnabledAsync(id, enabled: false);

        AssertEx.False(result!.Enabled);
        await store.DidNotReceive().SetEnabledAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
        await manager.DidNotReceive().RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetEnabledAsync_WhenAlreadyEnabled_ForcesAReconnectOfThatServerWithoutAStoreWrite()
    {
        // The UI's Reconnect: a plain refresh returned early for a live-looking but stuck server, so the service must force it.
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: true) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await service.SetEnabledAsync(id, enabled: true);

        AssertEx.True(result!.Enabled);
        AssertEx.Equal(existing.Version, result.Version);
        await store.DidNotReceive().SetEnabledAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
        await manager.Received(1).ReconnectAsync(id, Arg.Any<CancellationToken>());
        await manager.DidNotReceive().RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetEnabledAsync_WhenServerMissing_ReturnsNull()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((McpServerRecord?)null);

        var result = await service.SetEnabledAsync(id, enabled: true);

        AssertEx.Null(result);
        await manager.DidNotReceive().RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_PreservesEnabledState_AndRefreshesWhenEnabled()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: true) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        store.UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => existing with
             {
                 Command = ((McpServerInput)callInfo[1]!).Command,
                 Enabled = ((McpServerInput)callInfo[1]!).Enabled,
                 Version = existing.Version + 1
             });

        // The request body carries Enabled = false, but the service must preserve the current enabled (true).
        var requestInput = CreateStdioInput("Filesystem", "npx-new") with
        {
            Enabled = false
        };
        var result = await service.UpdateAsync(id, requestInput);

        AssertEx.True(result!.Enabled, "Update must preserve the current enabled state, not take it from the request body.");
        await store.Received(1).UpdateAsync(id, Arg.Is<McpServerInput>(input => input.Enabled), Arg.Any<CancellationToken>());
        // The server is enabled, so a config change refreshes the live snapshot.
        await manager.Received(1).RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_WhenEnabledAndTheStoreKeptTheVersion_DoesNotReconnect()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: true) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        // The store bumps Version only when a connection-relevant field changed; a save of the same values keeps it.
        store.UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).Returns(existing);

        var result = await service.UpdateAsync(id, CreateStdioInput());

        AssertEx.NotNull(result);
        await store.Received(1).UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
        await manager.DidNotReceive().RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_WhenEnabledAndOnlyRenamed_RefreshesSoStatusTextsCarryTheNewName()
    {
        // A rename keeps Version, and status and failure texts kept naming the old server until an unrelated reconnect.
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: true) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        store.UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).Returns(existing with
        {
            Name = "Renamed"
        });

        _ = await service.UpdateAsync(id, CreateStdioInput("Renamed"));

        await manager.Received(1).RefreshAsync(id, Arg.Any<CancellationToken>());
        await manager.DidNotReceive().ReconnectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_WhenDisabled_DoesNotRefresh()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: false) with
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

        var result = await service.UpdateAsync(id, CreateStdioInput("Filesystem", "npx-new"));

        AssertEx.NotNull(result);
        await manager.DidNotReceive().RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_WhenServerMissing_ReturnsNull()
    {
        var service = CreateService(out var store, out _);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((McpServerRecord?)null);

        var result = await service.UpdateAsync(id, CreateStdioInput());

        AssertEx.Null(result);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
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
        store.SetEnabledAsync(id, enabled: true, Arg.Any<CancellationToken>()).Returns(existing with
        {
            Enabled = true
        });
        manager.RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => throw new InvalidOperationException("connect failed"));

        var result = await service.SetEnabledAsync(id, enabled: true);

        AssertEx.True(result!.Enabled, "The toggle is committed even though the post-change refresh faulted.");
        await manager.Received(1).RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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
        store.SetEnabledAsync(id, enabled: true, Arg.Any<CancellationToken>()).Returns(existing with
        {
            Enabled = true
        });
        manager.RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => throw new OperationCanceledException());

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => service.SetEnabledAsync(id, enabled: true));
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

        var deleted = await service.DeleteAsync(id);

        AssertEx.True(deleted);
        await manager.Received(1).RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

        var deleted = await service.DeleteAsync(id);

        AssertEx.True(deleted);
        await manager.DidNotReceive().RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAsync_WhenServerMissing_ReturnsFalse()
    {
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((McpServerRecord?)null);

        var deleted = await service.DeleteAsync(id);

        AssertEx.False(deleted);
        await store.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await manager.DidNotReceive().RefreshAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static McpServerService CreateService(out IMcpServerStore store, out IMcpServerConnectionManager manager)
    {
        store = Substitute.For<IMcpServerStore>();
        manager = Substitute.For<IMcpServerConnectionManager>();
        var options = Options.Create(new McpOptions());
        return new McpServerService(store, manager, options, NullLogger<McpServerService>.Instance);
    }

    [Test]
    public async Task CreateAsync_WithBuiltInTrustedTier_ThrowsValidation()
    {
        // BuiltInTrusted names a transport the ENGINE owns. Accepting it here would let anything holding a session
        // label a third-party executable as engine-owned, so it is refused rather than silently downgraded.
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput() with
        {
            TrustTier = McpTrustTier.BuiltInTrusted
        };

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithUndefinedTrustTier_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput() with
        {
            TrustTier = (McpTrustTier)99
        };

        await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithHttpTransport_NormalizesThePrivilegedTierAway()
    {
        // The tier answers "where does this server's process run", and an HTTP registration launches nothing. A row
        // persisted as PrivilegedHost would read as a host grant somebody made.
        var service = CreateService(out var store, out _);
        var input = CreateHttpInput("http://127.0.0.1:8931/sse") with
        {
            TrustTier = McpTrustTier.PrivilegedHost
        };
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => CreateRecord((McpServerInput)callInfo[0]!, enabled: false));

        _ = await service.CreateAsync(input);

        await store.Received(1)
                   .AddAsync(Arg.Is<McpServerInput>(stored => stored.TrustTier == McpTrustTier.Sandboxed), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithStdioPrivilegedHostTier_PersistsItVerbatim()
    {
        var service = CreateService(out var store, out _);
        var input = CreateStdioInput() with
        {
            TrustTier = McpTrustTier.PrivilegedHost
        };
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => CreateRecord((McpServerInput)callInfo[0]!, enabled: false));

        _ = await service.CreateAsync(input);

        await store.Received(1)
                   .AddAsync(Arg.Is<McpServerInput>(stored => stored.TrustTier == McpTrustTier.PrivilegedHost), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_WithMaskedEnvironmentValue_KeepsTheStoredSecret()
    {
        // The API never returns an environment VALUE, so a form that round-trips what it was shown submits the mask.
        // Writing the mask through would replace the secret with a placeholder and silently break the server.
        var service = CreateService(out var store, out _);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: false) with
        {
            Id = id,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["API_TOKEN"] = "the-real-secret",
                ["ROTATED"] = "old"
            }
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        store.UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => existing with
             {
                 Environment = ((McpServerInput)callInfo[1]!).Environment
             });

        var requestInput = CreateStdioInput() with
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["API_TOKEN"] = McpEnvironmentMask.Value,
                ["ROTATED"] = "new"
            }
        };

        var result = await service.UpdateAsync(id, requestInput);

        AssertEx.Equal("the-real-secret", result!.Environment["API_TOKEN"]);
        AssertEx.Equal("new", result.Environment["ROTATED"]);
    }

    [Test]
    public async Task UpdateAsync_WithAMaskedValueUnderANewKey_IsRefused()
    {
        // A NEW key carrying the mask was stored with the placeholder as its value, handing the server "***" as a credential.
        var service = CreateService(out var store, out _);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: false) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);

        var exception = await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.UpdateAsync(id, CreateStdioInput() with
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ADDED"] = McpEnvironmentMask.Value
            }
        }));

        AssertEx.Contains(exception.Message, "ADDED");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_WithATierChange_PersistsTheNewTier()
    {
        var service = CreateService(out var store, out _);
        var id = Guid.NewGuid();
        var existing = CreateRecord(CreateStdioInput(), enabled: false) with
        {
            Id = id
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        store.UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => existing with
             {
                 TrustTier = ((McpServerInput)callInfo[1]!).TrustTier
             });

        var result = await service.UpdateAsync(id, CreateStdioInput() with
        {
            TrustTier = McpTrustTier.PrivilegedHost
        });

        AssertEx.Equal(McpTrustTier.PrivilegedHost, result!.TrustTier);
    }

    [Test]
    public async Task CreateAsync_WithEnvironmentOnAnHttpServer_ThrowsValidation()
    {
        // O-D6 (e2): the HTTP transport launches nothing, so an environment was accepted, masked and silently never sent.
        var service = CreateService(out var store, out _);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var input = CreateHttpInput("http://127.0.0.1:8931/mcp") with
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer x"
            }
        };

        var exception = await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));

        AssertEx.True(exception.Message.Contains("headers", StringComparison.Ordinal), exception.Message);
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithUserInfoInTheUrl_ThrowsValidation()
    {
        // O-D6 (e3): user:token@ was stored and shown in cleartext and never sent.
        var service = CreateService(out var store, out _);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        _ = await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(CreateHttpInput("http://user:token@127.0.0.1:8931/mcp")));
        await store.DidNotReceive().AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WithHeadersOnAStdioServer_ThrowsValidation()
    {
        var service = CreateService(out var store, out _);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var input = CreateStdioInput() with
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer x"
            }
        };

        _ = await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
    }

    [Test]
    [Arguments("Bad Name", "v")]
    [Arguments("", "v")]
    [Arguments("Host", "example")]
    [Arguments("content-length", "1")]
    [Arguments("X-Api-Key", "line\r\nInjected: yes")]
    public async Task CreateAsync_WithAnInvalidHeader_ThrowsValidation(string name, string value)
    {
        var service = CreateService(out var store, out _);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var input = CreateHttpInput("http://127.0.0.1:8931/mcp") with
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [name] = value
            }
        };

        _ = await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WithAHeaderValueOver4KB_ThrowsValidation_AndAt4KBPersists()
    {
        var service = CreateService(out var store, out _);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        var atLimit = CreateHttpInput("http://127.0.0.1:8931/mcp") with
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = new string('a', 4096)
            }
        };
        store.AddAsync(Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>()).Returns(CreateRecord(atLimit, enabled: false));

        _ = await service.CreateAsync(atLimit);
        _ = await AssertEx.ThrowsAsync<McpServerValidationException>(() => service.CreateAsync(atLimit with
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = new string('a', 4097)
            }
        }));
    }

    [Test]
    public async Task UpdateAsync_WithMaskedHeaderAndMaskedUrl_KeepsTheStoredCredentialAndUrl()
    {
        // The API returns header values masked and query values as ***, so a form round-tripping what it was shown must
        // keep the stored credential and the stored ?token= URL (existing rows stay PUT-able), while a real edit replaces them.
        var service = CreateService(out var store, out var manager);
        var id = Guid.NewGuid();
        const string storedUrl = "http://127.0.0.1:18912/mcp?token=s3cr3t&mode=a";
        var existing = CreateRecord(CreateHttpInput(storedUrl), enabled: true) with
        {
            Id = id,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer the-real-secret"
            }
        };
        store.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        store.UpdateAsync(id, Arg.Any<McpServerInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => existing with
             {
                 Headers = ((McpServerInput)callInfo[1]!).Headers,
                 Url = ((McpServerInput)callInfo[1]!).Url
             });

        var roundTripped = await service.UpdateAsync(id, CreateHttpInput(McpUrlMask.Mask(storedUrl)) with
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = McpEnvironmentMask.Value
            }
        });

        AssertEx.Equal("Bearer the-real-secret", roundTripped!.Headers["Authorization"]);
        AssertEx.Equal(storedUrl, roundTripped.Url);
        // The restored values equal the stored ones, so the store keeps Version and the live session is not reconnected.
        await manager.DidNotReceive().RefreshAsync(id, Arg.Any<CancellationToken>());

        var edited = await service.UpdateAsync(id, CreateHttpInput("http://127.0.0.1:18912/mcp") with
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = "Bearer rotated"
            }
        });

        AssertEx.Equal("Bearer rotated", edited!.Headers["Authorization"]);
        AssertEx.Equal("http://127.0.0.1:18912/mcp", edited.Url);
    }

    private static McpServerInput CreateStdioInput(string name = "Filesystem",
        string? command = "npx",
        bool enabled = false)
    {
        return new McpServerInput
        {
            Name = name,
            Description = "A filesystem MCP server.",
            TransportKind = McpTransportKind.Stdio,
            Command = command,
            Arguments = ["-y", "@modelcontextprotocol/server-filesystem"],
            WorkingDirectory = null,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Url = null,
            TrustTier = McpTrustTier.Sandboxed,
            Enabled = enabled
        };
    }

    private static McpServerInput CreateHttpInput(string? url, string name = "RemoteTools", bool enabled = false)
    {
        return new McpServerInput
        {
            Name = name,
            Description = null,
            TransportKind = McpTransportKind.Http,
            Command = null,
            Arguments = [],
            WorkingDirectory = null,
            Environment = new Dictionary<string, string>(StringComparer.Ordinal),
            Url = url,
            TrustTier = McpTrustTier.Sandboxed,
            Enabled = enabled
        };
    }

    private static McpServerRecord CreateRecord(McpServerInput input, bool enabled)
    {
        return new McpServerRecord
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = input.Description,
            TransportKind = input.TransportKind,
            Command = input.Command,
            Arguments = input.Arguments,
            WorkingDirectory = input.WorkingDirectory,
            Environment = input.Environment,
            Url = input.Url,
            TrustTier = input.TrustTier,
            Enabled = enabled,
            Version = 1,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10
        };
    }
}
