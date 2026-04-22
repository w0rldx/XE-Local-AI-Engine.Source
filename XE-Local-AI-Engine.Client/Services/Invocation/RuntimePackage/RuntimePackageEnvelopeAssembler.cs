namespace XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;

using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;
using Org.BouncyCastle.Crypto;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;

public sealed class RuntimePackageEnvelopeAssembler : IRuntimePackageEnvelopeAssembler
{
    private readonly IEnvelopeCryptoService _envelopeCryptoService;
    private readonly INodeKeyRegistry _nodeKeyRegistry;
    private readonly IRuntimePackageValidator _runtimePackageValidator;

    public RuntimePackageEnvelopeAssembler(IEnvelopeCryptoService envelopeCryptoService,
        INodeKeyRegistry nodeKeyRegistry,
        IRuntimePackageValidator runtimePackageValidator)
    {
        _envelopeCryptoService = envelopeCryptoService ?? throw new ArgumentNullException(nameof(envelopeCryptoService));
        _nodeKeyRegistry = nodeKeyRegistry ?? throw new ArgumentNullException(nameof(nodeKeyRegistry));
        _runtimePackageValidator = runtimePackageValidator ?? throw new ArgumentNullException(nameof(runtimePackageValidator));
    }

    public InvocationExecutionContext Assemble(EncryptedRuntimePackageDto package)
    {
        ArgumentNullException.ThrowIfNull(package);

        VerifyConfigHash(package);
        VerifyConversationContextHash(package);

        var historyMessages = DecryptHistoryMessages(package);
        using var decryptionResult = DecryptCurrentMessage(package);

        var runtimePackage = BuildRuntimePackage(package, historyMessages, decryptionResult.Plaintext.Span);
        ValidateRuntimePackage(runtimePackage);

        return InvocationExecutionContext.Create(runtimePackage,
            package.MessageId,
            package.EpochVersion,
            decryptionResult.EpochKey);
    }

    private static void VerifyConfigHash(EncryptedRuntimePackageDto package)
    {
        var computedHash = RuntimePackageConfigHash.Compute(package);
        if (string.Equals(computedHash, package.ConfigHash, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("runtime-package-config-hash-mismatch");
    }

    private static void VerifyConversationContextHash(EncryptedRuntimePackageDto package)
    {
        var computedHash = RuntimePackageHistoryHash.Compute(package.ConversationContext);
        if (string.Equals(computedHash, package.ConversationContextHash, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("runtime-package-history-hash-mismatch");
    }

    private List<ConversationMessageDto> DecryptHistoryMessages(EncryptedRuntimePackageDto package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var decryptedMessages = package.ConversationContext
                                       .Select(historyEntry => DecryptHistoryMessage(package.ConversationId, historyEntry))
                                       .OrderBy(static message => message.SortOrder)
                                       .ThenBy(static message => message.Id.ToString("D"), StringComparer.Ordinal)
                                       .ToList();

        return decryptedMessages;
    }

    private ConversationMessageDto DecryptHistoryMessage(Guid conversationId, EncryptedConversationMessageDto historyEntry)
    {
        ArgumentNullException.ThrowIfNull(historyEntry);

        var resolutions = _nodeKeyRegistry.ResolveGraceEligible();
        foreach (var resolution in resolutions)
        {
            if (!resolution.IsResolved || resolution.PrivateKey is null)
            {
                continue;
            }

            var decryptionResult = TryDecryptHistoryMessage(conversationId, historyEntry, resolution.PrivateKey);
            if (decryptionResult is null)
            {
                continue;
            }

            using (decryptionResult)
            {
                return new ConversationMessageDto
                {
                    Id = historyEntry.Id,
                    Role = historyEntry.Role,
                    Content = Encoding.UTF8.GetString(decryptionResult.Plaintext.Span),
                    SortOrder = historyEntry.SortOrder
                };
            }
        }

        throw new InvalidOperationException($"No grace-eligible node key could decrypt history message {historyEntry.Id}.");
    }

    private EnvelopeDecryptionResult? TryDecryptHistoryMessage(Guid conversationId, EncryptedConversationMessageDto historyEntry, Key nodePrivateKey)
    {
        try
        {
            return _envelopeCryptoService.DecryptConversationMessage(conversationId, historyEntry, nodePrivateKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (InvalidCipherTextException)
        {
            return null;
        }
    }

    private EnvelopeDecryptionResult DecryptCurrentMessage(EncryptedRuntimePackageDto package)
    {
        var resolutions = _nodeKeyRegistry.ResolveGraceEligible();
        if (resolutions.Count == 0)
        {
            throw new InvalidOperationException($"No grace-eligible node key is available to decrypt message {package.MessageId}.");
        }

        foreach (var resolution in resolutions)
        {
            if (!resolution.IsResolved || resolution.PrivateKey is null)
            {
                continue;
            }

            var decryptionResult = TryDecryptCurrentMessage(package, resolution.PrivateKey);
            if (decryptionResult is not null)
            {
                return decryptionResult;
            }
        }

        throw new InvalidOperationException($"No grace-eligible node key could decrypt message {package.MessageId}.");
    }

    private EnvelopeDecryptionResult? TryDecryptCurrentMessage(EncryptedRuntimePackageDto package, Key nodePrivateKey)
    {
        try
        {
            return _envelopeCryptoService.DecryptRuntimePackage(package, nodePrivateKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (InvalidCipherTextException)
        {
            return null;
        }
    }

    private static RuntimePackage BuildRuntimePackage(EncryptedRuntimePackageDto package,
        IReadOnlyList<ConversationMessageDto> historyMessages,
        ReadOnlySpan<byte> currentMessagePlaintext)
    {
        var currentMessageSortOrder = historyMessages.Count == 0 ? 1 : historyMessages.Max(static message => message.SortOrder) + 1;

        return new RuntimePackage
        {
            InvocationId = package.InvocationId,
            ConversationId = package.ConversationId,
            ClientNodeId = package.ClientNodeId,
            AgentDefinitionVersion = package.AgentDefinitionVersion,
            ResolvedSystemPrompt = package.ResolvedSystemPrompt,
            ConversationContext =
            [
                .. historyMessages,
                new ConversationMessageDto
                {
                    Id = package.MessageId,
                    Role = MessageRole.User,
                    Content = Encoding.UTF8.GetString(currentMessagePlaintext),
                    SortOrder = currentMessageSortOrder
                }
            ],
            AllowedTools = [.. package.AllowedTools.Select(MapAllowedTool)],
            ModelProfile = package.ModelProfile,
            Timeouts = package.Timeouts,
            ConfigHash = package.ConfigHash
        };
    }

    private static AllowedToolDto MapAllowedTool(MixedEnvelopeAllowedToolDto tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return new AllowedToolDto
        {
            Id = Guid.Empty,
            Name = tool.Name,
            Location = ToolLocation.ApiSide,
            ParameterSchema = tool.Schema
        };
    }

    private void ValidateRuntimePackage(RuntimePackage runtimePackage)
    {
        var validationResult = _runtimePackageValidator.Validate(runtimePackage);
        if (validationResult.IsValid)
        {
            return;
        }

        throw new InvalidOperationException(string.Join("; ", validationResult.Errors));
    }
}
