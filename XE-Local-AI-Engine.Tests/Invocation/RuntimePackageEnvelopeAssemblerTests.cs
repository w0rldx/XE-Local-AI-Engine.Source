namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimePackageEnvelopeAssemblerTests
{
    private static readonly JsonSerializerOptions PascalCaseRoleSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.Default,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Test]
    public void Assemble_WhenConfigHashMatches_BuildsInvocationExecutionContext()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        envelopeCryptoService.DecryptRuntimePackage(Arg.Any<EncryptedRuntimePackageDto>(), nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("hello"u8.ToArray(), new byte[32]));

        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);

        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);
        using var context = assembler.Assemble(CreatePackage(true));

        AssertEx.Equal("hello", context.Package.ConversationContext[0].Content);
        AssertEx.Equal(expected: 1, context.Package.ConversationContext.Count);
        AssertEx.Equal(MessageRole.User, context.Package.ConversationContext[0].Role);
        validator.Received(1).Validate(Arg.Any<RuntimePackage>());
    }

    [Test]
    public void Assemble_WhenWireToolIsClientLocal_CarriesLocationAndApprovalIntoRuntimePackage()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        envelopeCryptoService.DecryptRuntimePackage(Arg.Any<EncryptedRuntimePackageDto>(), nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("hello"u8.ToArray(), new byte[32]));

        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);

        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);
        using var context = assembler.Assemble(CreateClientLocalPackage());

        // The wire DTO must drive routing: a ClientLocal/approval-required tool that round-trips the encrypted envelope
        // arrives as ClientLocal (not the previously hardcoded ApiSide), so BuildInvocationTools emits a
        // CreateOfferPlaceholder instead of an API-side bridge or the "Unsupported tool location" throw.
        var tool = context.Package.AllowedTools[0];
        AssertEx.Equal("run_in_agent_home", tool.Name);
        AssertEx.Equal(ToolLocation.ClientLocal, tool.Location);
        AssertEx.True(tool.RequiresApproval, "Wire RequiresApproval must survive the assembler mapping.");
        AssertEx.Equal(Guid.Empty, tool.Id);
    }

    [Test]
    public async Task Assemble_WhenConfigHashMismatches_ThrowsAndDoesNotDecrypt()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        var validator = Substitute.For<IRuntimePackageValidator>();
        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => assembler.Assemble(CreatePackage(false))));

        AssertEx.Equal("runtime-package-config-hash-mismatch", exception.Message);
        envelopeCryptoService.DidNotReceive().DecryptRuntimePackage(Arg.Any<EncryptedRuntimePackageDto>(), Arg.Any<Key>());
        validator.DidNotReceive().Validate(Arg.Any<RuntimePackage>());
    }

    [Test]
    public void Assemble_WhenHistoryExists_DecryptsHistoryAndAppendsCurrentMessageLast()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var historyEntry = CreateHistoryEntry(5);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var package = CreatePackage(includeValidConfigHash: true, historyEntries: [historyEntry]);
        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        envelopeCryptoService.DecryptConversationMessage(package.ConversationId, historyEntry, nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("earlier"u8.ToArray(), new byte[32]));
        envelopeCryptoService.DecryptRuntimePackage(package, nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("latest"u8.ToArray(), new byte[32]));

        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);

        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);
        using var context = assembler.Assemble(package);

        AssertEx.Equal(expected: 2, context.Package.ConversationContext.Count);
        AssertEx.Equal(historyEntry.Id, context.Package.ConversationContext[0].Id);
        AssertEx.Equal("earlier", context.Package.ConversationContext[0].Content);
        AssertEx.Equal(expected: 5, context.Package.ConversationContext[0].SortOrder);
        AssertEx.Equal(package.MessageId, context.Package.ConversationContext[1].Id);
        AssertEx.Equal("latest", context.Package.ConversationContext[1].Content);
        AssertEx.Equal(expected: 6, context.Package.ConversationContext[1].SortOrder);

        // Replayed tool history is a LOOPBACK-only construction: it is attached by the local turn assembler and has no
        // representation on the wire, because the encrypted history entry the cross-repo hash is computed over carries
        // no field for it. An inbound package therefore always decrypts to none, whatever the caller sent.
        AssertEx.True(context.Package.ConversationContext.All(static message => message.ToolExchanges is null),
            "An API-side package can never carry tool history.");
    }

    [Test]
    public void Assemble_WhenMultiMessageHistoryHashMatches_DecryptsAllHistoryEntriesInCanonicalOrder()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var laterEntry = CreateHistoryEntry(20);
        var earlierEntry = CreateHistoryEntry(sortOrder: 10, MessageRole.User);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var package = CreatePackage(includeValidConfigHash: true, historyEntries: [laterEntry, earlierEntry]);
        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        envelopeCryptoService.DecryptConversationMessage(package.ConversationId, earlierEntry, nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("first user turn"u8.ToArray(), new byte[32]));
        envelopeCryptoService.DecryptConversationMessage(package.ConversationId, laterEntry, nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("assistant reply"u8.ToArray(), new byte[32]));
        envelopeCryptoService.DecryptRuntimePackage(package, nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("latest user turn"u8.ToArray(), new byte[32]));

        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);

        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);
        using var context = assembler.Assemble(package);

        AssertEx.Equal(expected: 3, context.Package.ConversationContext.Count);
        AssertEx.Equal(earlierEntry.Id, context.Package.ConversationContext[0].Id);
        AssertEx.Equal(MessageRole.User, context.Package.ConversationContext[0].Role);
        AssertEx.Equal("first user turn", context.Package.ConversationContext[0].Content);
        AssertEx.Equal(expected: 10, context.Package.ConversationContext[0].SortOrder);
        AssertEx.Equal(laterEntry.Id, context.Package.ConversationContext[1].Id);
        AssertEx.Equal(MessageRole.Assistant, context.Package.ConversationContext[1].Role);
        AssertEx.Equal("assistant reply", context.Package.ConversationContext[1].Content);
        AssertEx.Equal(expected: 20, context.Package.ConversationContext[1].SortOrder);
        AssertEx.Equal(package.MessageId, context.Package.ConversationContext[2].Id);
        AssertEx.Equal(MessageRole.User, context.Package.ConversationContext[2].Role);
        AssertEx.Equal("latest user turn", context.Package.ConversationContext[2].Content);
        AssertEx.Equal(expected: 21, context.Package.ConversationContext[2].SortOrder);
    }

    [Test]
    public void Assemble_WhenHistoryEntryUsesDifferentEpoch_DecryptsCrossEpochHistory()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var historyEntry = CreateHistoryEntry(sortOrder: 5, epochVersion: 3);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var package = CreatePackage(includeValidConfigHash: true, historyEntries: [historyEntry]);
        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        envelopeCryptoService.DecryptConversationMessage(package.ConversationId, historyEntry, nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("from prior epoch"u8.ToArray(), new byte[32]));
        envelopeCryptoService.DecryptRuntimePackage(package, nodePrivateKey)
                             .Returns(_ => new EnvelopeDecryptionResult("current epoch"u8.ToArray(), new byte[32]));

        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);

        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);
        using var context = assembler.Assemble(package);

        AssertEx.Equal(expected: 2, context.Package.ConversationContext.Count);
        AssertEx.Equal(historyEntry.Id, context.Package.ConversationContext[0].Id);
        AssertEx.Equal("from prior epoch", context.Package.ConversationContext[0].Content);
        AssertEx.Equal(MessageRole.Assistant, context.Package.ConversationContext[0].Role);
        AssertEx.Equal(package.MessageId, context.Package.ConversationContext[1].Id);
        AssertEx.Equal("current epoch", context.Package.ConversationContext[1].Content);
        AssertEx.Equal(package.EpochVersion, context.EpochVersion);
        envelopeCryptoService.Received(1).DecryptConversationMessage(package.ConversationId, Arg.Is<EncryptedConversationMessageDto>(entry => entry.Id == historyEntry.Id && entry.EpochVersion == 3),
            nodePrivateKey);
    }

    [Test]
    public async Task Assemble_WhenConversationContextHashMismatches_ThrowsAndDoesNotDecrypt()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        var validator = Substitute.For<IRuntimePackageValidator>();
        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => assembler.Assemble(CreatePackage(includeValidConfigHash: true, includeValidHistoryHash: false))));

        AssertEx.Equal("runtime-package-history-hash-mismatch", exception.Message);
        envelopeCryptoService.DidNotReceive().DecryptRuntimePackage(Arg.Any<EncryptedRuntimePackageDto>(), Arg.Any<Key>());
        validator.DidNotReceive().Validate(Arg.Any<RuntimePackage>());
    }

    [Test]
    public async Task Assemble_WhenHistoryHashWasComputedWithPascalCaseRoleNames_ThrowsAndDoesNotDecrypt()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var firstHistoryEntry = CreateHistoryEntry(sortOrder: 10, MessageRole.User);
        var secondHistoryEntry = CreateHistoryEntry(20);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        var validator = Substitute.For<IRuntimePackageValidator>();
        var package = CreatePackage(includeValidConfigHash: true, historyEntries: [firstHistoryEntry, secondHistoryEntry]);
        package = package with
        {
            ConversationContextHash = ComputeHistoryHashWithPascalCaseRoleNames(package.ConversationContext)
        };
        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => assembler.Assemble(package)));

        AssertEx.Equal("runtime-package-history-hash-mismatch", exception.Message);
        envelopeCryptoService.DidNotReceive().DecryptConversationMessage(Arg.Any<Guid>(), Arg.Any<EncryptedConversationMessageDto>(), Arg.Any<Key>());
        envelopeCryptoService.DidNotReceive().DecryptRuntimePackage(Arg.Any<EncryptedRuntimePackageDto>(), Arg.Any<Key>());
        validator.DidNotReceive().Validate(Arg.Any<RuntimePackage>());
    }

    [Test]
    public async Task Assemble_WhenHistoryEntryCannotBeDecrypted_ThrowsAndDoesNotDecryptCurrentMessage()
    {
        using var nodePrivateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        var historyEntry = CreateHistoryEntry(5);
        var nodeKeyRegistry = Substitute.For<INodeKeyRegistry>();
        nodeKeyRegistry.ResolveGraceEligible().Returns([
            new NodeKeyResolution
            {
                RequestedKeyId = "active-key",
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = "active-key",
                PrivateKey = nodePrivateKey,
                PublicKey = nodePrivateKey.PublicKey
            }
        ]);

        var package = CreatePackage(includeValidConfigHash: true, historyEntries: [historyEntry]);
        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        envelopeCryptoService.DecryptConversationMessage(package.ConversationId, historyEntry, nodePrivateKey)
                             .Returns(_ => throw new CryptographicException("bad-history-entry"));

        var validator = Substitute.For<IRuntimePackageValidator>();
        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => assembler.Assemble(package)));

        AssertEx.Equal($"No grace-eligible node key could decrypt history message {historyEntry.Id}.", exception.Message);
        envelopeCryptoService.DidNotReceive().DecryptRuntimePackage(Arg.Any<EncryptedRuntimePackageDto>(), Arg.Any<Key>());
        validator.DidNotReceive().Validate(Arg.Any<RuntimePackage>());
    }

    private static EncryptedRuntimePackageDto CreatePackage(bool includeValidConfigHash,
        bool includeValidHistoryHash = true,
        IReadOnlyList<EncryptedConversationMessageDto>? historyEntries = null)
    {
        var conversationContext = historyEntries?.ToList() ?? [];
        var package = new EncryptedRuntimePackageDto
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            ClientNodeId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            EpochVersion = 7,
            AgentDefinitionVersion = 7,
            ResolvedSystemPrompt = "You are a helpful local AI assistant.",
            AllowedTools =
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\"}"
                }
            ],
            ModelProfile = null,
            Timeouts = new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            },
            ConfigHash = string.Empty,
            ConversationContext = conversationContext,
            ConversationContextHash = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945",
            NodeWrappedEpochKey = new byte[]
            {
                1,
                2,
                3
            },
            ClientEphemeralPublicKey = new byte[]
            {
                4,
                5,
                6
            },
            Ciphertext = new byte[]
            {
                7,
                8,
                9
            },
            ContentIv = new byte[]
            {
                10,
                11,
                12
            },
            Aad = "message|aad-placeholder"
        };

        var computedHash = RuntimePackageConfigHash.Compute(package);
        return package with
        {
            ConfigHash = includeValidConfigHash ? computedHash : "invalid-config-hash",
            ConversationContextHash = includeValidHistoryHash
                ? RuntimePackageHistoryHash.Compute(package.ConversationContext)
                : "invalid-history-hash"
        };
    }

    private static EncryptedRuntimePackageDto CreateClientLocalPackage()
    {
        var package = new EncryptedRuntimePackageDto
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            ClientNodeId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            EpochVersion = 7,
            AgentDefinitionVersion = 7,
            ResolvedSystemPrompt = "You are a helpful local AI assistant.",
            AllowedTools =
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "run_in_agent_home",
                    Description = "Run a task in the agent home workspace",
                    Schema = "{\"type\":\"object\"}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = true
                }
            ],
            ModelProfile = null,
            Timeouts = new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            },
            ConfigHash = string.Empty,
            ConversationContext = [],
            ConversationContextHash = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945",
            NodeWrappedEpochKey = new byte[]
            {
                1,
                2,
                3
            },
            ClientEphemeralPublicKey = new byte[]
            {
                4,
                5,
                6
            },
            Ciphertext = new byte[]
            {
                7,
                8,
                9
            },
            ContentIv = new byte[]
            {
                10,
                11,
                12
            },
            Aad = "message|aad-placeholder"
        };

        return package with
        {
            ConfigHash = RuntimePackageConfigHash.Compute(package),
            ConversationContextHash = RuntimePackageHistoryHash.Compute(package.ConversationContext)
        };
    }

    private static EncryptedConversationMessageDto CreateHistoryEntry(int sortOrder, MessageRole role = MessageRole.Assistant, int epochVersion = 7)
    {
        return new EncryptedConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = role,
            SortOrder = sortOrder,
            EpochVersion = epochVersion,
            Aad = "message|history-placeholder",
            NodeWrappedEpochKey = new byte[]
            {
                1,
                2,
                3
            },
            ClientEphemeralPublicKey = new byte[]
            {
                4,
                5,
                6
            },
            Ciphertext = new byte[]
            {
                7,
                8,
                9
            },
            ContentIv = new byte[]
            {
                10,
                11,
                12
            }
        };
    }

    private static string ComputeHistoryHashWithPascalCaseRoleNames(IReadOnlyList<EncryptedConversationMessageDto> conversationContext)
    {
        var orderedEntries = conversationContext
                             .OrderBy(static entry => entry.SortOrder)
                             .ThenBy(static entry => entry.Id.ToString("D"), StringComparer.Ordinal)
                             .ToList();

        var canonicalJson = JsonSerializer.Serialize(orderedEntries, PascalCaseRoleSerializerOptions);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }
}
